using System.Text;
using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;

namespace Quality.Tests;

public sealed class ExecutionRequestTests
{
    [Fact]
    public async Task ValidManifestRequiresExactApprovalBeforeLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-execution-request-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jobs = new FileJobStore(Path.Combine(root, "jobs"), TimeProvider.System);
            await jobs.InitializeAsync(default);
            var plan = Plan();
            var now = DateTimeOffset.UtcNow;
            var job = new QualityJob(Guid.NewGuid().ToString("N"), new("stub", "REQ-1"), JobStatus.Completed,
                now, now, 0, null, plan, [], [], null, null, null);
            await jobs.CreateAsync(job, default);
            var executor = new CapturingExecutor();
            var service = new ExecutionRequestService(new FileExecutionRequestStore(Path.Combine(root, "requests")), jobs,
                executor, new(root, ["http://127.0.0.1:8000"]), TimeProvider.System);

            var request = await service.CreateAsync(job.Id, "http://127.0.0.1:8000/", default);
            Assert.Equal(ExecutionRequestStatus.Draft, request.Status);
            await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAsync(request.Id, request.Revision,
                request.ManifestHash, "reviewer", default));

            var manifest = new ExecutionManifest(plan.Id, ExecutionManifest.HashPlan(plan), request.Target,
                [new("TC-1", [new("goto", null, "/"), new("expectVisible", "h1", null)])]);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ContractJson.Options));
            request = await service.UpdateManifestAsync(request.Id, request.Revision, bytes, default);
            Assert.Equal(ExecutionRequestStatus.AwaitingApproval, request.Status);
            await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAsync(request.Id, request.Revision,
                new string('0', 64), "reviewer", default));

            request = await service.ApproveAsync(request.Id, request.Revision, request.ManifestHash, "reviewer", default);
            Assert.Equal(ExecutionRequestStatus.Approved, request.Status);
            request = await service.LaunchAsync(request.Id, request.Revision, default);
            Assert.Equal(ExecutionRequestStatus.Queued, request.Status);
            request = (await service.ProcessNextAsync(default))!;

            Assert.Equal(ExecutionRequestStatus.Passed, request.Status);
            Assert.Equal(executor.RunId, request.RunId);
            Assert.NotNull(executor.Manifest);
            Assert.Equal(manifest.TestPlanId, executor.Manifest.TestPlanId);
            Assert.Equal(manifest.PlanHash, executor.Manifest.PlanHash);
            Assert.Equal(manifest.Target, executor.Manifest.Target);
            Assert.Equal(manifest.Tests[0].TestCaseId, executor.Manifest.Tests[0].TestCaseId);
            Assert.Equal(manifest.Tests[0].Steps, executor.Manifest.Tests[0].Steps);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExpiredRunningRequestIsFailedWithoutReplayingActions()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-execution-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileExecutionRequestStore(Path.Combine(root, "requests"));
            var now = DateTimeOffset.UtcNow;
            var request = new ExecutionRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "plan", "http://127.0.0.1:8000/",
                ExecutionRequestStatus.Running, "{}", new string('0', 64), 0, now.AddMinutes(-20), now.AddMinutes(-20),
                LeaseToken: "old", LeaseUntil: now.AddMinutes(-10), Attempts: 1);
            await store.CreateAsync(request, default);

            var recovered = await store.ClaimAsync(TimeSpan.FromMinutes(10), default);

            Assert.Equal(ExecutionRequestStatus.InfrastructureFailed, recovered!.Status);
            Assert.Equal("execution_worker_interrupted", recovered.Error);
            Assert.Null(recovered.LeaseToken);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentManifestEditIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-execution-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jobs = new FileJobStore(Path.Combine(root, "jobs"), TimeProvider.System);
            await jobs.InitializeAsync(default);
            var plan = Plan();
            var now = DateTimeOffset.UtcNow;
            var job = new QualityJob(Guid.NewGuid().ToString("N"), new("stub", "REQ-1"), JobStatus.Completed,
                now, now, 0, null, plan, [], [], null, null, null);
            await jobs.CreateAsync(job, default);
            var service = new ExecutionRequestService(new FileExecutionRequestStore(Path.Combine(root, "requests")), jobs,
                new CapturingExecutor(), new(root, ["http://127.0.0.1:8000"]), TimeProvider.System);
            var request = await service.CreateAsync(job.Id, "http://127.0.0.1:8000/", default);
            var manifest = new ExecutionManifest(plan.Id, ExecutionManifest.HashPlan(plan), request.Target,
                [new("TC-1", [new("expectVisible", "h1", null)])]);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ContractJson.Options));
            await service.UpdateManifestAsync(request.Id, request.Revision, bytes, default);

            await Assert.ThrowsAsync<ExecutionRequestConflictException>(() =>
                service.UpdateManifestAsync(request.Id, request.Revision, bytes, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrepareManifestBuildsReadOnlyAssertionsAndStillRequiresApproval()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-execution-prepare-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jobs = new FileJobStore(Path.Combine(root, "jobs"), TimeProvider.System);
            await jobs.InitializeAsync(default);
            var plan = Plan();
            var now = DateTimeOffset.UtcNow;
            var job = new QualityJob(Guid.NewGuid().ToString("N"), new("stub", "REQ-1"), JobStatus.Completed,
                now, now, 0, null, plan, [], [], null, null, null);
            await jobs.CreateAsync(job, default);
            var service = new ExecutionRequestService(new FileExecutionRequestStore(Path.Combine(root, "requests")), jobs,
                new CapturingExecutor(), new(root, ["http://127.0.0.1:8000"]), TimeProvider.System);
            var request = await service.CreateAsync(job.Id, "http://127.0.0.1:8000/", default);

            request = await service.PrepareManifestAsync(request.Id, request.Revision,
                new StaticInspector(new("http://127.0.0.1:8000/", [new("h1", "h1", "Quality Command Center", "h1"), new("input", "input", "Issue key", "#jira-key")])), default);

            Assert.Equal(ExecutionRequestStatus.AwaitingApproval, request.Status);
            var manifest = JsonSerializer.Deserialize<ExecutionManifest>(request.ManifestJson, ContractJson.Options)!;
            Assert.Equal(["goto", "expectText", "expectVisible"], manifest.Tests.Single().Steps.Select(step => step.Action));
            Assert.Equal("Quality Command Center", manifest.Tests.Single().Steps[1].Value);
            Assert.Equal("#jira-key", manifest.Tests.Single().Steps[2].Selector);
            Assert.DoesNotContain(manifest.Tests.Single().Steps, step => step.Action is "click" or "fill");
            await Assert.ThrowsAsync<ArgumentException>(() => service.LaunchAsync(request.Id, request.Revision, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ManifestBuilderMapsOnlyExplicitPlannedInteractionsToCapableControls()
    {
        var plan = new TestPlan("plan-steps", "requirement-1", "Plan",
            [new("TC-1", "requirement-1", "Submit", "HappyPath", "P1", ["AC-1"],
                [new("Enter Issue key with \"KAN-5\"", "The issue key is present"),
                 new("Click Generate plan", "A plan is generated"),
                 new("Complete the workflow", "Success")])], [], [], "plan/v2", false);
        var inspection = new UiInspection("http://127.0.0.1:8000/", [
            new("h1", "h1", "Quality Command Center", "h1"),
            new("input", "input", "Issue key", "#jira-key", ["fill"]),
            new("button", "button", "Generate plan", "#generate-plan", ["click"]) ]);

        var manifest = ReviewedManifestBuilder.Build(plan, "http://127.0.0.1:8000/", inspection);

        Assert.Equal(["goto", "expectText", "fill", "click", "expectVisible"], manifest.Tests.Single().Steps.Select(step => step.Action));
        Assert.Contains(manifest.Tests.Single().Steps, step => step.Action == "fill" && step.Selector == "#jira-key" && step.Value == "KAN-5");
        Assert.Contains(manifest.Tests.Single().Steps, step => step.Action == "click" && step.Selector == "#generate-plan");
    }

    [Fact]
    public async Task AutonomousPolicyPreparesApprovesAndQueuesOnlyAllowedManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-execution-autonomous-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jobs = new FileJobStore(Path.Combine(root, "jobs"), TimeProvider.System);
            await jobs.InitializeAsync(default);
            var plan = Plan();
            var now = DateTimeOffset.UtcNow;
            var job = new QualityJob(Guid.NewGuid().ToString("N"), new("stub", "REQ-1"), JobStatus.Completed,
                now, now, 0, null, plan, [], [], null, null, null);
            await jobs.CreateAsync(job, default);
            var policy = new ExecutionPolicy("command-center-smoke", "v1", ["http://127.0.0.1:8000"],
                ["goto", "expectText", "expectVisible"], NonProduction: true, AutoApprove: true, AutoLaunch: true,
                CanaryMaxAutoLaunches: 1);
            var service = new ExecutionRequestService(new FileExecutionRequestStore(Path.Combine(root, "requests")), jobs,
                new CapturingExecutor(), new(root, ["http://127.0.0.1:8000"]), TimeProvider.System,
                policies: new ExecutionPolicyCatalog([policy]));

            var request = await service.CreateAutonomousAsync(job.Id, "http://127.0.0.1:8000/", policy.Name,
                new StaticInspector(new("http://127.0.0.1:8000/", [new("h1", "h1", "Quality", "h1"), new("input", "input", "Issue", "#jira-key")])), default);

            Assert.Equal(ExecutionRequestStatus.Queued, request.Status);
            Assert.Equal(policy.Name, request.AutomationPolicy);
            Assert.Equal(policy.Version, request.AutomationPolicyVersion);
            Assert.Equal(policy.Fingerprint(), request.AutomationPolicyHash);
            Assert.Equal(policy.ReviewerIdentity(), request.Approval!.Reviewer);

            var held = await service.CreateAutonomousAsync(job.Id, "http://127.0.0.1:8000/", policy.Name,
                new StaticInspector(new("http://127.0.0.1:8000/", [new("h1", "h1", "Quality", "h1"), new("input", "input", "Issue", "#jira-key")])), default);
            Assert.Equal(ExecutionRequestStatus.Approved, held.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AutonomousPolicyRejectsProductionAttestationAndUndeclaredActions()
    {
        var manifest = new ExecutionManifest("plan-1", ExecutionManifest.HashPlan(Plan()), "http://127.0.0.1:8000/",
            [new("TC-1", [new("goto", null, "/"), new("click", "#save", null), new("expectVisible", "h1", null)])]);
        var production = new ExecutionPolicy("not-production", "v1", ["http://127.0.0.1:8000"],
            ["goto", "click", "expectVisible"], NonProduction: false, AutoApprove: true);
        var restrictive = new ExecutionPolicy("assertions-only", "v1", ["http://127.0.0.1:8000"],
            ["goto", "expectVisible"], NonProduction: true, AutoApprove: true);

        Assert.Throws<ArgumentException>(() => production.ValidateManifest(manifest));
        Assert.Throws<ArgumentException>(() => restrictive.ValidateManifest(manifest));
    }

    private static TestPlan Plan() => new("plan-1", "requirement-1", "Plan",
        [new("TC-1", "requirement-1", "Page", "HappyPath", "P1", ["AC-1"], [new("Open", "Visible")])],
        [], [], "plan/v2", false);

    [PostgresFact]
    public async Task PostgreSqlClaimsExecutionOnceAndPersistsSharedRunMetadata()
    {
        await MigrationTests.InSchema(async source =>
        {
            var jobs = new PostgresJobStore(source);
            await jobs.InitializeAsync(default);
            var now = DateTimeOffset.UtcNow;
            var job = new QualityJob(Guid.NewGuid().ToString("N"), new("stub", "REQ-1"), JobStatus.Completed,
                now, now, 0, null, Plan(), [], [], null, null, null);
            await jobs.CreateAsync(job, default);
            var requests = new PostgresExecutionRequestStore(source);
            var item = new ExecutionRequest(Guid.NewGuid().ToString("N"), job.Id, job.TestPlan!.Id,
                "http://127.0.0.1:8000/", ExecutionRequestStatus.Queued, "{}", new string('0', 64), 0, now, now);
            await requests.CreateAsync(item, default);

            var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => requests.ClaimAsync(TimeSpan.FromMinutes(10), default)));
            Assert.Single(claims, x => x is not null);
            Assert.Equal(ExecutionRequestStatus.Running, Assert.Single(claims, x => x is not null)!.Status);

            var root = Path.Combine(Path.GetTempPath(), "quality-postgres-runs-" + Guid.NewGuid().ToString("N"));
            try
            {
                var runs = new PostgresTestRunStore(source, root);
                var run = new TestRun(Guid.NewGuid().ToString("N"), job.TestPlan.Id, "Passed", now, now, ["artifact.json"], "test");
                await runs.SaveAsync(run, default);
                var saved = await runs.GetAsync(run.Id, default);
                Assert.Equal(run.Id, saved!.Id);
                Assert.Equal(run.Status, saved.Status);
                Assert.Equal(run.ArtifactKeys, saved.ArtifactKeys);
                Assert.Equal(run.Id, Assert.Single(await runs.ListByPlanAsync(job.TestPlan.Id, default)).Id);
                Assert.True(File.Exists(Path.Combine(runs.DirectoryFor(run.Id), "run.json")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    private sealed class CapturingExecutor : IReviewedTestExecutor
    {
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public ExecutionManifest? Manifest { get; private set; }
        public Task<TestRun> ExecuteAsync(TestPlan plan, Uri baseUrl, CancellationToken ct) => throw new NotSupportedException();
        public Task<TestRun> ExecuteReviewedAsync(TestPlan plan, byte[] manifestBytes, string reviewedSha256, CancellationToken ct)
        {
            Assert.Equal(reviewedSha256, ExecutionManifest.Hash(manifestBytes));
            Manifest = JsonSerializer.Deserialize<ExecutionManifest>(manifestBytes, ContractJson.Options);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new TestRun(RunId, plan.Id, "Passed", now, now, [], "test", ManifestHash: reviewedSha256));
        }
    }

    private sealed class StaticInspector(UiInspection result) : IUiInspector
    {
        public Task<UiInspection> InspectAsync(Uri target, CancellationToken ct) => Task.FromResult(result);
    }
}
