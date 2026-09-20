using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Quality.Orchestrator;

public sealed record SourceControlOptions(string Workspace, string ProposalDirectory, string? TargetRepository = null);

// Produces a real staged Git diff in an isolated repository. Never modifies/pushes the working repository.
public sealed class GitPatchSourceControl(SourceControlOptions options) : ISourceControl, IRegressionProposalManager
{
    private sealed record ProposalFile(string Path, string Sha256);
    private sealed record ProposalDocument(string Branch, string Title, string Status, string PatchSha256,
        string? TargetRepository, ProposalFile[] Files, DateTimeOffset? AppliedAt = null);
    public async Task<string> ProposeAsync(string branch, string title, IReadOnlyList<SourceChange> changes, CancellationToken ct)
    {
        if (!Regex.IsMatch(branch, "^regression/[a-f0-9]{32}$") || string.IsNullOrWhiteSpace(title) || changes.Count == 0)
            throw new ArgumentException("Invalid regression proposal");
        foreach (var change in changes)
        {
            if (!Regex.IsMatch(change.Path, "^playwright/regressions/REG-[a-f0-9]{24}/(manifest.json|mapping.json|test.spec.cjs)$"))
                throw new ArgumentException("Source change is outside the regression namespace");
            if (File.Exists(Path.Combine(options.Workspace, change.Path)) || Directory.Exists(Path.Combine(options.Workspace, change.Path)))
                throw new ArgumentException("Existing regression cannot be overwritten; review an explicit source change instead");
        }
        if (changes.Select(c => c.Path).Distinct().Count() != changes.Count) throw new ArgumentException("Duplicate proposal paths");
        var root = Path.GetFullPath(options.ProposalDirectory);
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, branch.Split('/')[1]);
        if (Directory.Exists(destination)) throw new ArgumentException("A proposal already exists for this run");
        var temporary = Path.Combine(root, ".building-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(temporary, "changes");
        Directory.CreateDirectory(checkout);
        try
        {
            await Git(checkout, ct, "init", "--quiet", "--initial-branch=" + branch);
            foreach (var change in changes)
            {
                var file = Path.Combine(checkout, change.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, change.Content, ct);
            }
            await Git(checkout, ct, "add", "--", "playwright/regressions");
            var patch = await Git(checkout, ct, "diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "--no-textconv");
            if (string.IsNullOrWhiteSpace(patch)) throw new InvalidOperationException("Proposal diff is empty");
            await File.WriteAllTextAsync(Path.Combine(temporary, "proposal.patch"), patch, ct);
            var document = new ProposalDocument(branch, title, "NeedsReview",
                ExecutionManifest.Hash(System.Text.Encoding.UTF8.GetBytes(patch)),
                options.TargetRepository ?? options.Workspace,
                changes.Select(c => new ProposalFile(c.Path, ExecutionManifest.Hash(System.Text.Encoding.UTF8.GetBytes(c.Content)))).ToArray());
            await WriteDocumentAsync(Path.Combine(temporary, "proposal.json"), document, ct);
            Directory.Move(temporary, destination);
            return Path.Combine(destination, "proposal.patch");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public async Task<RegressionProposal?> GetAsync(string runId, CancellationToken ct)
    {
        ValidateRunId(runId);
        var directory = ProposalDirectory(runId);
        var documentPath = Path.Combine(directory, "proposal.json");
        var patchPath = Path.Combine(directory, "proposal.patch");
        if (!File.Exists(documentPath) || !File.Exists(patchPath)) return null;
        if (new FileInfo(documentPath).Length > 1024 * 1024 || new FileInfo(patchPath).Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Regression proposal exceeds the review limit");
        var document = JsonSerializer.Deserialize<ProposalDocument>(await File.ReadAllTextAsync(documentPath, ct), JsonOptions())
            ?? throw new InvalidOperationException("Regression proposal metadata is invalid");
        var patch = await File.ReadAllTextAsync(patchPath, ct);
        if (document.Branch != "regression/" + runId || document.Files.Length == 0 ||
            ExecutionManifest.Hash(System.Text.Encoding.UTF8.GetBytes(patch)) != document.PatchSha256)
            throw new InvalidOperationException("Regression proposal integrity check failed");
        var changes = new List<SourceChange>();
        foreach (var file in document.Files)
        {
            ValidateRegressionPath(file.Path);
            var changePath = Path.Combine(directory, "changes", file.Path);
            if (!File.Exists(changePath) || new FileInfo(changePath).Length > 1024 * 1024) throw new InvalidOperationException("Proposed file is missing or too large");
            var content = await File.ReadAllTextAsync(changePath, ct);
            if (ExecutionManifest.Hash(System.Text.Encoding.UTF8.GetBytes(content)) != file.Sha256)
                throw new InvalidOperationException("Proposed file integrity check failed");
            changes.Add(new(file.Path, content));
        }
        return new(runId, document.Title, document.Status, document.PatchSha256,
            document.TargetRepository ?? options.TargetRepository ?? options.Workspace, changes, patch, document.AppliedAt);
    }

    public async Task<RegressionProposal> ApplyAsync(string runId, string reviewedPatchSha256, CancellationToken ct)
    {
        ValidateRunId(runId);
        if (!Regex.IsMatch(reviewedPatchSha256 ?? "", "^[a-f0-9]{64}$")) throw new ArgumentException("A reviewed patch SHA-256 is required");
        var directory = ProposalDirectory(runId);
        Directory.CreateDirectory(directory);
        using var gate = new FileStream(Path.Combine(directory, ".apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var proposal = await GetAsync(runId, ct) ?? throw new ArgumentException("Regression proposal not found");
        if (proposal.Status != "NeedsReview") throw new ArgumentException("Only a proposal awaiting review can be applied");
        if (proposal.PatchSha256 != reviewedPatchSha256) throw new ArgumentException("Reviewed patch SHA-256 does not match the proposal");
        var workspace = Path.GetFullPath(options.Workspace);
        if (!string.Equals(proposal.TargetRepository, options.TargetRepository ?? options.Workspace, StringComparison.Ordinal))
            throw new ArgumentException("Configured regression repository changed after this proposal was created");
        var repositoryPrefix = (await Git(workspace, ct, "rev-parse", "--show-prefix")).Trim();
        if (repositoryPrefix.Length != 0)
            throw new ArgumentException("Configured regression repository must be a Git repository root");
        foreach (var file in proposal.Files)
        {
            ValidateRegressionPath(file.Path);
            var destination = Path.GetFullPath(Path.Combine(workspace, file.Path));
            if (!destination.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal) || File.Exists(destination) || Directory.Exists(destination))
                throw new ArgumentException("Existing regression cannot be overwritten");
            for (var parent = Path.GetDirectoryName(destination); parent is not null && parent.StartsWith(workspace, StringComparison.Ordinal); parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Regression destination cannot contain symbolic links");
        }
        var patchPath = Path.Combine(directory, "proposal.patch");
        await Git(workspace, ct, "apply", "--check", "--whitespace=error-all", patchPath);
        await Git(workspace, ct, "apply", "--whitespace=error-all", patchPath);
        var documentPath = Path.Combine(directory, "proposal.json");
        var document = JsonSerializer.Deserialize<ProposalDocument>(await File.ReadAllTextAsync(documentPath, ct), JsonOptions())!;
        await WriteDocumentAsync(documentPath, document with { Status = "Applied", AppliedAt = DateTimeOffset.UtcNow }, ct);
        return (await GetAsync(runId, ct))!;
    }

    private string ProposalDirectory(string runId) => Path.Combine(Path.GetFullPath(options.ProposalDirectory), runId);
    private static void ValidateRunId(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Invalid run ID");
    }
    private static void ValidateRegressionPath(string path)
    {
        if (!Regex.IsMatch(path, "^playwright/regressions/REG-[a-f0-9]{24}/(manifest.json|mapping.json|test.spec.cjs)$"))
            throw new ArgumentException("Source change is outside the regression namespace");
    }
    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static Task WriteDocumentAsync(string path, ProposalDocument document, CancellationToken ct)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonOptions()), ct);
    private static async Task<string> Git(string directory, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Remove inherited repository/index/config overrides and disable hooks, filters and external diff commands.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        foreach (var arg in new[] { "-c", "core.hooksPath=/dev/null", "-c", "core.autocrlf=false" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git proposal operation failed");
        return await stdout;
    }
}
