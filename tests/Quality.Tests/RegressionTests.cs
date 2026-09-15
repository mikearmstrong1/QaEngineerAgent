using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class RegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-promotion-" + Guid.NewGuid().ToString("N"));
    private FileTestRunStore Runs => new(root);
    private sealed class Capture : ISourceControl
    {
        public IReadOnlyList<SourceChange>? Changes;
        public Task<string> ProposeAsync(string branch, string title, IReadOnlyList<SourceChange> changes, CancellationToken ct)
        { Changes = changes; return Task.FromResult("proposal.patch"); }
    }
    private async Task<(QualityJob Job, TestRun Run)> Fixture()
    {
        var reference = new RequirementReference("jira", "REG-1");
        var requirement = new Requirement("jira:REG-1", reference, "Page", "Page heading", [], [], [new("AC-1", "Heading says Ready")], [], "revision-1", false);
        var plan = new TestPlan("plan-1", requirement.Id, "Reviewed page coverage", [new("TC-1", requirement.Id, "Page", "HappyPath", "P1", ["AC-1"], [new("Open page", "Heading says Ready")])], [], [], "plan/v2", false);
        var job = QualityJob.Create(new(reference), DateTimeOffset.UtcNow) with { Status = JobStatus.Completed, Requirement = requirement, TestPlan = plan };
        var manifest = new ExecutionManifest(plan.Id, ExecutionManifest.HashPlan(plan), "http://localhost:3000", [new("TC-1", [new("goto", null, "/"), new("expectText", "h1", "Ready")])]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options);
        var run = new TestRun(Guid.NewGuid().ToString("N"), plan.Id, "Passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], "fixture",
            ManifestHash: ExecutionManifest.Hash(bytes), TestCaseIds: ["TC-1"]);
        await Runs.SaveAsync(run, default);
        await File.WriteAllBytesAsync(Path.Combine(Runs.DirectoryFor(run.Id), "manifest.json"), bytes);
        await File.WriteAllTextAsync(Path.Combine(Runs.DirectoryFor(run.Id), "origins.json"), "[\"http://localhost:3000\"]");
        return (job, run);
    }
    [Fact]
    public async Task PromotionKeepsAssertionsAndRequirementMappings()
    {
        var (job, run) = await Fixture();
        var source = new Capture();
        await new RegressionPromotion(Runs, source).ProposeAsync(job, run.Id, run.ManifestHash!, default);
        Assert.Equal(3, source.Changes!.Count);
        var manifest = JsonSerializer.Deserialize<ExecutionManifest>(source.Changes.Single(c => c.Path.EndsWith("manifest.json")).Content, ContractJson.Options)!;
        Assert.Equal("Ready", manifest.Tests[0].Steps[1].Value);
        Assert.Equal("expectText", manifest.Tests[0].Steps[1].Action);
        using var mapping = JsonDocument.Parse(source.Changes.Single(c => c.Path.EndsWith("mapping.json")).Content);
        Assert.Equal("AC-1", mapping.RootElement.GetProperty("acceptanceCriteria")[0].GetProperty("id").GetString());
        Assert.Equal("revision-1", mapping.RootElement.GetProperty("sourceRevision").GetString());
        Assert.Equal(RegressionPromotion.StableId(job.Requirement!.Id, "TC-1"), mapping.RootElement.GetProperty("stableId").GetString());
        Assert.NotEqual(RegressionPromotion.StableId(job.Requirement.Id, "TC-1"), RegressionPromotion.StableId("jira:OTHER-1", "TC-1"));
    }
    [Theory]
    [InlineData("failed")]
    [InlineData("stub")]
    [InlineData("hash")]
    [InlineData("wrong-plan")]
    [InlineData("missing-criterion")]
    [InlineData("wrong-tests")]
    public async Task IneligibleRunsNeverProposeOrRepair(string kind)
    {
        var (job, run) = await Fixture();
        var hash = run.ManifestHash!;
        switch (kind)
        {
            case "failed": run = run with { Status = "Failed" }; break;
            case "stub": job = job with { Requirement = job.Requirement! with { IsStub = true } }; break;
            case "hash": hash = new string('0', 64); break;
            case "wrong-plan": run = run with { TestPlanId = "another-plan" }; break;
            case "missing-criterion": job = job with { Requirement = job.Requirement! with { AcceptanceCriteria = [] } }; break;
            case "wrong-tests": run = run with { TestCaseIds = ["TC-2"] }; break;
        }
        await Runs.SaveAsync(run, default);
        var before = await File.ReadAllBytesAsync(Path.Combine(Runs.DirectoryFor(run.Id), "manifest.json"));
        var source = new Capture();
        await Assert.ThrowsAsync<ArgumentException>(() => new RegressionPromotion(Runs, source).ProposeAsync(job, run.Id, hash, default));
        Assert.Null(source.Changes);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(Runs.DirectoryFor(run.Id), "manifest.json")));
    }
    [Fact]
    public async Task ClassificationIsExplicitAndDoesNotChangeTestOutcome()
    {
        var (_, run) = await Fixture();
        var service = new RegressionPromotion(Runs, new Capture());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ClassifyFailureAsync(run.Id, "ApplicationFailure", "review", default));
        await Runs.SaveAsync(run with { Status = "Failed", FailureClassification = "NeedsReview" }, default);
        var reviewed = await service.ClassifyFailureAsync(run.Id, "ApplicationFailure", "Confirmed against the acceptance criterion", default);
        Assert.Equal("Failed", reviewed.Status);
        Assert.Equal("ApplicationFailure", reviewed.FailureClassification);
        Assert.Single(Directory.GetFiles(Path.Combine(Runs.DirectoryFor(run.Id), "reviews")));
    }
    [Fact]
    public async Task SourceControlRejectsTraversalAndExistingAssertions()
    {
        var source = new GitPatchSourceControl(new(root, Path.Combine(root, "proposals")));
        var branch = "regression/" + Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<ArgumentException>(() => source.ProposeAsync(branch, "bad path", [new("../file", "bad")], default));
        var file = "playwright/regressions/REG-" + new string('a', 24) + "/manifest.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
        await File.WriteAllTextAsync(Path.Combine(root, file), "existing assertion");
        await Assert.ThrowsAsync<ArgumentException>(() => source.ProposeAsync(branch, "overwrite", [new(file, "weaker assertion")], default));
        Assert.Equal("existing assertion", await File.ReadAllTextAsync(Path.Combine(root, file)));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
