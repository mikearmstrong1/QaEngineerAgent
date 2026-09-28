using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quality.Domain;
namespace Quality.Orchestrator;

public sealed class RunArtifactPublisher(ITestRunStore runs, IArtifactStore artifacts)
{
    public async Task<TestRun> PublishAsync(string runId, CancellationToken ct,
        EvidencePublicationRequirements? requirements = null)
    {
        var run = await runs.GetAsync(runId, ct) ?? throw new ArgumentException("Run not found");
        if (run.FinishedAt is null || run.Status == "Running") throw new ArgumentException("Only finished runs can be published");
        var directory = runs.DirectoryFor(runId);
        // Serialize publishers across processes, so partial retries cannot overwrite each other's progress.
        using var gate = new FileStream(Path.Combine(directory, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        run = (await runs.GetAsync(runId, ct))!;
        requirements ??= run.EvidenceRequirements;
        ValidateRequirements(run, requirements);
        var uploaded = (run.StoredArtifacts ?? []).ToDictionary(a => a.LocalKey, StringComparer.Ordinal);
        run = run with { ArtifactUploadStatus = "Uploading", EvidenceRequirements = requirements };
        await runs.SaveAsync(run, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            foreach (var localKey in run.ArtifactKeys)
            {
                if (!localKey.StartsWith(runId + "/", StringComparison.Ordinal)) throw new InvalidDataException("Artifact belongs to another run");
                var relative = localKey[(runId.Length + 1)..];
                if (relative.Split('/').Any(segment => segment is "" or "." or "..") || relative.Contains('\\'))
                    throw new InvalidDataException("Invalid local artifact key");
                var path = Path.Combine(directory, relative);
                // Reject links in every component; a run cannot upload files outside its evidence folder.
                var current = directory;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked run directory");
                foreach (var segment in relative.Split('/'))
                {
                    current = Path.Combine(current, segment);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked artifact path");
                }
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, timeout.Token)).ToLowerInvariant();
                source.Position = 0;
                var redactionStatus = await VerifyRedactionAsync(source, path, timeout.Token);
                source.Position = 0;
                var key = $"{MinioArtifactStore.Prefix}runs/{runId}/{hash}/{relative}";
                // Re-PUT the same content-addressed key on retry: safe after a lost acknowledgement or expired object.
                var handle = await artifacts.PutAsync(key, source, ContentType(path), timeout.Token);
                if (handle.Sha256 != hash || handle.Length != source.Length) throw new InvalidDataException("Artifact changed during upload");
                var verification = new ArtifactVerification(hash, handle.Sha256, handle.Sha256 == hash,
                    redactionStatus, DateTimeOffset.UtcNow);
                uploaded[localKey] = new(localKey, handle.Key, handle.Bucket, handle.ContentType, handle.Length,
                    handle.Sha256, handle.Provider, verification);
                run = run with { StoredArtifacts = uploaded.Values.OrderBy(a => a.LocalKey, StringComparer.Ordinal).ToArray() };
                await runs.SaveAsync(run, timeout.Token);
            }
            run = run with { ArtifactUploadStatus = "Uploaded" };
        }
        catch (Exception)
        {
            // Keep execution outcome and successful uploads; raw provider errors may contain sensitive data.
            run = run with { ArtifactUploadStatus = "Failed" };
        }
        await runs.SaveAsync(run, CancellationToken.None);
        return run;
    }

    private void ValidateRequirements(TestRun run, EvidencePublicationRequirements? requirements)
    {
        if (requirements is null) return;
        if (requirements.RetentionDays is < 1 or > 3650)
            throw new ArgumentException("Evidence retention must be 1-3650 days");
        if (requirements.RequiredArtifactPatterns is null || requirements.RequiredArtifactPatterns.Length > 20
            || requirements.RequiredArtifactPatterns.Any(pattern => string.IsNullOrWhiteSpace(pattern)
                || pattern.Length > 200 || pattern.Any(char.IsControl) || pattern.Contains('/') || pattern.Contains('\\')))
            throw new ArgumentException("Required artifact patterns must be 1-200 character file-name patterns");
        foreach (var pattern in requirements.RequiredArtifactPatterns)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (!run.ArtifactKeys.Any(key => Regex.IsMatch(Path.GetFileName(key), regex, RegexOptions.CultureInvariant)))
                throw new InvalidDataException($"Required evidence is missing: {pattern}");
        }
        if (requirements.RetentionDays is { } days && artifacts is IRemoteArtifactStore remote
            && (!remote.Info.RetentionManaged || remote.Info.RetentionDays < days))
            throw new InvalidOperationException("Artifact provider does not satisfy the evidence retention requirement");
    }

    private static async Task<string> VerifyRedactionAsync(Stream source, string path, CancellationToken ct)
    {
        var pattern = new Regex(
            "(?im)(authorization|proxy-authorization|cookie|set-cookie)\\s*[:=]|(api[_-]?key|access[_-]?token|client[_-]?secret)\\s*[:=]|https?://[^\\s/@:]+:[^\\s/@]+@|[?&](token|key|secret|password)=[^&#\\s]+",
            RegexOptions.CultureInvariant);
        var buffer = new byte[64 * 1024];
        var carry = "";
        int read;
        // Binary evidence is scanned as bounded UTF-8 chunks for embedded metadata/URLs. This is not OCR.
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            var text = carry + Encoding.UTF8.GetString(buffer, 0, read);
            if (pattern.IsMatch(text)) throw new InvalidDataException("Artifact failed redaction verification");
            carry = text.Length <= 512 ? text : text[^512..];
        }
        return IsText(path) ? "TextScanPassed" : "BinaryMetadataScanPassed";
    }

    private static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".json" or ".html" or ".md" or ".log" or ".txt" or ".cjs" or ".js";

    public async Task<FailureAnalysis> AssociateFailureAsync(FailureAnalysis analysis, CancellationToken ct)
    {
        if (analysis.SchemaVersion != "1.0" || string.IsNullOrWhiteSpace(analysis.Classification) || string.IsNullOrWhiteSpace(analysis.Summary)
            || !double.IsFinite(analysis.Confidence) || analysis.Confidence is < 0 or > 1)
            throw new ArgumentException("Invalid failure analysis fields");
        if (!Guid.TryParseExact(analysis.Id, "N", out _) || analysis.EvidenceArtifactKeys is null || analysis.EvidenceArtifactKeys.Length == 0)
            throw new ArgumentException("Failure analysis requires a valid ID and evidence keys");
        var run = await runs.GetAsync(analysis.TestRunId, ct) ?? throw new ArgumentException("Run not found");
        if (run.FinishedAt is null) throw new ArgumentException("Run has not finished");
        var references = analysis.EvidenceArtifactKeys.Select(key => (run.StoredArtifacts ?? [])
            .SingleOrDefault(a => a.LocalKey == key || a.ObjectKey == key)
            ?? throw new ArgumentException("Failure evidence must be an uploaded artifact of this run")).Distinct().ToArray();
        if (run.ArtifactUploadStatus != "Uploaded" || references.Any(reference => reference.Verification is not { ChecksumVerified: true }
            || reference.Verification.SourceSha256 != reference.Verification.StoredSha256
            || reference.Verification.RedactionStatus is not ("TextScanPassed" or "BinaryMetadataScanPassed")))
            throw new ArgumentException("Failure evidence must pass checksum and redaction verification");
        var associated = analysis with { EvidenceArtifactKeys = references.Select(a => a.ObjectKey).ToArray(), EvidenceArtifacts = references };
        var directory = Path.Combine(runs.DirectoryFor(run.Id), "analyses");
        Directory.CreateDirectory(directory);
        // Immutable association files; a new review gets a new analysis ID.
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(associated, ContractJson.Options), ct);
            File.Move(temporary, Path.Combine(directory, analysis.Id + ".json"), false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return associated;
    }
    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".zip" => "application/zip",
        ".json" => "application/json", ".html" => "text/html; charset=utf-8", ".md" => "text/markdown; charset=utf-8",
        ".log" or ".txt" => "text/plain; charset=utf-8", ".cjs" or ".js" => "text/javascript; charset=utf-8",
        _ => "application/octet-stream"
    };
}
