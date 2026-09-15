using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Quality.Orchestrator;

public sealed record SourceControlOptions(string Workspace, string ProposalDirectory);

// Produces a real staged Git diff in an isolated repository. Never modifies/pushes the working repository.
public sealed class GitPatchSourceControl(SourceControlOptions options) : ISourceControl
{
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
            await File.WriteAllTextAsync(Path.Combine(temporary, "proposal.json"), JsonSerializer.Serialize(new
            {
                branch, title, status = "NeedsReview", patchSha256 = ExecutionManifest.Hash(System.Text.Encoding.UTF8.GetBytes(patch)),
                files = changes.Select(c => new { path = c.Path, sha256 = ExecutionManifest.Hash(System.Text.Encoding.UTF8.GetBytes(c.Content)) })
            }, new JsonSerializerOptions { WriteIndented = true }), ct);
            Directory.Move(temporary, destination);
            return Path.Combine(destination, "proposal.patch");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
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
