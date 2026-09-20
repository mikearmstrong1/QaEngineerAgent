using Quality.Domain;

namespace Quality.Orchestrator;

public sealed record ArtifactContent(Stream Stream, string ContentType, long Length, string FileName);

public sealed class ArtifactContentReader(ITestRunStore runs, IArtifactStore artifacts)
{
    public async Task<ArtifactContent?> OpenAsync(string runId, string key, CancellationToken ct)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Invalid run ID");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1000 || key.Any(char.IsControl))
            throw new ArgumentException("Invalid artifact key");
        var run = await runs.GetAsync(runId, ct);
        if (run is null) return null;
        var stored = (run.StoredArtifacts ?? []).SingleOrDefault(item => item.LocalKey == key || item.ObjectKey == key);
        var localKey = stored?.LocalKey ?? key;
        if (!run.ArtifactKeys.Contains(localKey, StringComparer.Ordinal) || !localKey.StartsWith(runId + "/", StringComparison.Ordinal))
            throw new ArgumentException("Artifact does not belong to this run");
        var relative = localKey[(runId.Length + 1)..];
        if (relative.Split('/').Any(segment => segment is "" or "." or "..") || relative.Contains('\\'))
            throw new ArgumentException("Invalid artifact key");

        var directory = runs.DirectoryFor(runId);
        var path = Path.Combine(directory, relative);
        if (File.Exists(path))
        {
            RejectLinks(directory, relative);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(stream, stored?.ContentType ?? RunArtifactPublisher.ContentType(path), stream.Length, Path.GetFileName(path));
        }

        if (stored is null || artifacts.Provider == "Local"
            || stored.Provider is not null && !string.Equals(stored.Provider, artifacts.Provider, StringComparison.Ordinal))
            return null;
        var remote = await artifacts.OpenReadAsync(stored.ObjectKey, ct);
        return new(remote, stored.ContentType, stored.Length, Path.GetFileName(stored.LocalKey));
    }

    private static void RejectLinks(string directory, string relative)
    {
        var current = directory;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked run directory");
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked artifact path");
        }
    }
}
