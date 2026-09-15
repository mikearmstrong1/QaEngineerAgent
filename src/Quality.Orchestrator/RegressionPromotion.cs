using System.Text;
using System.Text.Json;
using Quality.Domain;
namespace Quality.Orchestrator;

public sealed class RegressionPromotion(ITestRunStore runs, ISourceControl sourceControl)
{
    public static string StableId(string requirementId, string testCaseId)
        => "REG-" + ExecutionManifest.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { requirementId, testCaseId })))[..24];

    public async Task<string> ProposeAsync(QualityJob job, string runId, string reviewedManifestHash, CancellationToken ct)
    {
        var run = await runs.GetAsync(runId, ct) ?? throw new ArgumentException("Run not found");
        if (job.Status != JobStatus.Completed || job.Requirement is null || job.TestPlan is null)
            throw new ArgumentException("A completed requirement and plan are required");
        if (job.Requirement.IsStub || job.TestPlan.IsStub)
            throw new ArgumentException("Synthetic plans cannot be promoted into regression coverage");
        if (run.Status != "Passed" || run.FinishedAt is null || run.TestPlanId != job.TestPlan.Id)
            throw new ArgumentException("Only a passing run of this plan can be promoted");
        var path = Path.Combine(runs.DirectoryFor(run.Id), "manifest.json");
        if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("Manifest exceeds the promotion limit");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (ExecutionManifest.Hash(bytes) != run.ManifestHash || run.ManifestHash != reviewedManifestHash)
            throw new ArgumentException("Review hash does not match the executed manifest");
        var manifest = JsonSerializer.Deserialize<ExecutionManifest>(bytes, ContractJson.Options) ?? throw new ArgumentException("Manifest is required");
        var origins = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(runs.DirectoryFor(run.Id), "origins.json"), ct))!;
        manifest.Validate(job.TestPlan, origins);
        if (run.TestCaseIds is null || !run.TestCaseIds.Order().SequenceEqual(manifest.Tests.Select(t => t.TestCaseId).Order()))
            throw new ArgumentException("Executed test IDs do not match the reviewed manifest");
        var changes = new List<SourceChange>();
        foreach (var executed in manifest.Tests)
        {
            var planned = job.TestPlan.TestCases.Single(t => t.Id == executed.TestCaseId);
            var criteria = planned.AcceptanceCriterionIds.Select(id => job.Requirement.AcceptanceCriteria.SingleOrDefault(ac => ac.Id == id)
                ?? throw new ArgumentException("Planned test refers to a missing acceptance criterion")).ToArray();
            var stableId = StableId(job.Requirement.Id, planned.Id);
            var directory = "playwright/regressions/" + stableId;
            // Keep the exact actions/assertions, target and original test ID. Only the selected test subset changes.
            var selected = manifest with { Tests = [executed] };
            changes.Add(new(directory + "/manifest.json", JsonSerializer.Serialize(selected, ContractJson.Options) + "\n"));
            changes.Add(new(directory + "/mapping.json", JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0", stableId, testCaseId = planned.Id, requirementId = job.Requirement.Id,
                reference = job.Requirement.Reference, sourceRevision = job.Requirement.SourceRevision,
                acceptanceCriteria = criteria, testPlanId = job.TestPlan.Id, runId = run.Id,
                reviewedManifestHash, planHash = manifest.PlanHash,
                executionStatus = run.Status, executorVersion = run.ExecutorVersion,
                localEvidenceKeys = run.ArtifactKeys, remoteEvidence = run.StoredArtifacts ?? []
            }, ContractJson.Options) + "\n"));
            changes.Add(new(directory + "/test.spec.cjs", """
                const { test, expect } = require('@playwright/test');
                const { register } = require('../../execution/register.cjs');
                const origins = (process.env.QUALITY_REGRESSION_ORIGINS ?? '').split(';').filter(Boolean);
                register(require('./manifest.json'), origins, test, expect);
                """ + "\n"));
        }
        return await sourceControl.ProposeAsync("regression/" + run.Id, "Add reviewed regression coverage for " + job.Requirement.Reference.Id, changes, ct);
    }

    public async Task<TestRun> ClassifyFailureAsync(string runId, string classification, string reason, CancellationToken ct)
    {
        if (classification is not ("ApplicationFailure" or "TestFailure" or "InfrastructureFailure")
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 4000)
            throw new ArgumentException("Choose a failure category and supply a review reason of 1-4000 characters");
        var run = await runs.GetAsync(runId, ct) ?? throw new ArgumentException("Run not found");
        if (run.FinishedAt is null || run.Status is not ("Failed" or "TimedOut" or "InfrastructureFailed"))
            throw new ArgumentException("Only completed failing runs can be classified");
        using var gate = new FileStream(Path.Combine(runs.DirectoryFor(runId), ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        run = (await runs.GetAsync(runId, ct))!;
        var review = new { runId, classification, reason, reviewedAt = DateTimeOffset.UtcNow, manifestHash = run.ManifestHash };
        var directory = Path.Combine(runs.DirectoryFor(runId), "reviews");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(review, ContractJson.Options), ct);
        run = run with { FailureClassification = classification, FailureReviewReason = reason };
        await runs.SaveAsync(run, ct);
        return run;
    }
}
