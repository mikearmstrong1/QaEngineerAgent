using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using Quality.Domain;
namespace Quality.Orchestrator;

public sealed class PlaywrightTestExecutor(ExecutionOptions options, ITestRunStore store, TimeProvider clock) : IReviewedTestExecutor
{
    public Task<TestRun> ExecuteAsync(TestPlan plan, Uri baseUrl, CancellationToken ct)
        => throw new NotSupportedException("Execution requires a reviewed manifest and its SHA-256 digest");

    public async Task<TestRun> ExecuteReviewedAsync(TestPlan plan, byte[] manifestBytes, string reviewedSha256, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (manifestBytes.Length > 1024 * 1024 || ExecutionManifest.Hash(manifestBytes) != reviewedSha256)
            throw new ArgumentException("Manifest checksum does not match the reviewed bytes");
        using var schemaJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.Workspace, "schemas/v1/execution-manifest.schema.json"), ct));
        using var input = JsonDocument.Parse(manifestBytes);
        if (!JsonSchema.Build(schemaJson.RootElement).Evaluate(input.RootElement).IsValid)
            throw new ArgumentException("Execution manifest does not match its schema");
        RejectDuplicateKeys(input.RootElement);
        var jsonOptions = new JsonSerializerOptions(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var manifest = JsonSerializer.Deserialize<ExecutionManifest>(manifestBytes, jsonOptions)!;
        manifest.Validate(plan, options.AllowedOrigins);
        var runner = Path.Combine(Path.GetFullPath(options.Workspace), "playwright/execution/runner.cjs");
        if (!File.Exists(runner)) throw new ArgumentException("Execution workspace is missing the trusted runner");
        var run = new TestRun(Guid.NewGuid().ToString("N"), plan.Id, "Running", clock.GetUtcNow(), null, [], null,
            ManifestHash: reviewedSha256, TestCaseIds: manifest.Tests.Select(t => t.TestCaseId).ToArray());
        var directory = store.DirectoryFor(run.Id);
        await store.SaveAsync(run, ct);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), manifestBytes, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "origins.json"), JsonSerializer.Serialize(options.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin)), ct);
            var start = new ProcessStartInfo(options.NodeExecutable) { WorkingDirectory = Path.GetFullPath(options.Workspace), UseShellExecute = false };
            // Do not forward provider keys, custom Node hooks or arbitrary process configuration into the runner.
            var inherited = new Dictionary<string, string?>();
            foreach (var name in new[] { "PATH", "HOME", "TMPDIR", "TEMP", "TMP", "SystemRoot", "PLAYWRIGHT_BROWSERS_PATH" })
                inherited[name] = Environment.GetEnvironmentVariable(name);
            start.Environment.Clear();
            foreach (var (name, value) in inherited) if (value is not null) start.Environment[name] = value;
            start.ArgumentList.Add(runner);
            start.ArgumentList.Add(directory);
            using var process = new Process { StartInfo = start };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds + 15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                run = run with { Status = ct.IsCancellationRequested ? "Cancelled" : "TimedOut" };
            }
            if (run.Status == "Running")
            {
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json"), ct));
                var root = result.RootElement;
                var status = root.GetProperty("status").GetString();
                var ids = root.GetProperty("testCaseIds").EnumerateArray().Select(x => x.GetString()).ToArray();
                if (status is not ("Passed" or "Failed" or "TimedOut" or "InfrastructureFailed")
                    || (status == "Passed" && (process.ExitCode != 0 || !ids.Order().SequenceEqual(run.TestCaseIds!.Order()))))
                    throw new InvalidDataException("Invalid execution result");
                var classification = root.GetProperty("failureClassification").GetString();
                if (classification is not ("None" or "NeedsReview" or "TestFailure" or "InfrastructureFailure"))
                    throw new InvalidDataException("Invalid failure classification");
                run = run with { Status = status, ExecutorVersion = root.GetProperty("executorVersion").GetString(), FailureClassification = classification };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { run = run with { Status = "Cancelled" }; }
        catch (Exception) { run = run with { Status = "InfrastructureFailed" }; }
        if (run.Status is "TimedOut" or "InfrastructureFailed") run = run with { FailureClassification = "InfrastructureFailure" };
        if (run.Status == "Cancelled") run = run with { FailureClassification = "Cancelled" };
        run = run with { FinishedAt = clock.GetUtcNow(), ArtifactKeys = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("run.json", StringComparison.Ordinal)).Select(p => run.Id + "/" + Path.GetRelativePath(directory, p).Replace('\\', '/')).Order().ToArray() };
        // Completion is durable even when the caller cancels the execution.
        await store.SaveAsync(run, CancellationToken.None);
        return run;
    }
    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate manifest property");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
