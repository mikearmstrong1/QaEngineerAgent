using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;

namespace Quality.Tests;

public sealed class AutomationWorkflowTests
{
    [Fact]
    public async Task WorkflowIsIdempotentResumableAndCompletesWithoutDuplicateExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await Fixture.CreateAsync(root, autoApprove: true, autoLaunch: true);
            var workflow = await fixture.Workflows.CreateAsync(fixture.Job.Id, fixture.Target, fixture.Policy.Name,
                "deployment-42", default);
            var replay = await fixture.Workflows.CreateAsync(fixture.Job.Id, fixture.Target, fixture.Policy.Name,
                "deployment-42", default);
            Assert.Equal(workflow.Id, replay.Id);
            await Assert.ThrowsAsync<IdempotencyConflictException>(() => fixture.Workflows.CreateAsync(
                fixture.Job.Id, fixture.Target + "other", fixture.Policy.Name, "deployment-42", default));

            for (var index = 0; index < 3; index++) await fixture.Workflows.ProcessNextAsync(default);
            workflow = (await fixture.Workflows.GetAsync(workflow.Id, default))!;
            Assert.Equal(AutomationWorkflowStatus.ManifestPrepared, workflow.Status);

            // Reconstruct services over the same durable stores to simulate a process restart.
            fixture = await Fixture.CreateAsync(root, autoApprove: true, autoLaunch: true, existingJob: fixture.Job);
            while ((workflow = (await fixture.Workflows.GetAsync(workflow.Id, default))!).Status != AutomationWorkflowStatus.Queued)
                await fixture.Workflows.ProcessNextAsync(default);

            var request = await fixture.Executions.GetAsync(workflow.Id, default);
            Assert.NotNull(request);
            Assert.Equal(workflow.Id, request!.Id);
            Assert.Equal(ExecutionRequestStatus.Queued, request.Status);
            await fixture.Executions.ProcessNextAsync(default);
            for (var index = 0; index < 4; index++) await fixture.Workflows.ProcessNextAsync(default);

            workflow = (await fixture.Workflows.GetAsync(workflow.Id, default))!;
            Assert.Equal(AutomationWorkflowStatus.Completed, workflow.Status);
            Assert.Equal("None", workflow.Classification);
            Assert.Equal(1, fixture.Executor.Calls);
            Assert.Equal(workflow.Checkpoints.Select(item => item.Status).Distinct().Count(), workflow.Checkpoints.Length);
            Assert.Contains(workflow.Checkpoints, item => item.Status == AutomationWorkflowStatus.EvidencePublished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HumanReviewResumesTheSameWorkflowAndQueuesTheSameRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-workflow-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await Fixture.CreateAsync(root, autoApprove: false, autoLaunch: false);
            var workflow = await fixture.Workflows.CreateAsync(fixture.Job.Id, fixture.Target, fixture.Policy.Name,
                "manual-gate", default);
            for (var index = 0; index < 5; index++) await fixture.Workflows.ProcessNextAsync(default);
            workflow = (await fixture.Workflows.GetAsync(workflow.Id, default))!;
            Assert.Equal(AutomationWorkflowStatus.AwaitingReview, workflow.Status);
            Assert.Equal("policy_requires_review", workflow.EscalationReason);

            workflow = await fixture.Workflows.ReviewAsync(workflow.Id, workflow.Revision, true, "operator", default);
            Assert.Equal(AutomationWorkflowStatus.Approved, workflow.Status);
            await fixture.Workflows.ProcessNextAsync(default);

            workflow = (await fixture.Workflows.GetAsync(workflow.Id, default))!;
            Assert.Equal(AutomationWorkflowStatus.Queued, workflow.Status);
            Assert.Equal(workflow.Id, workflow.ExecutionRequestId);
            Assert.Equal("Approved", workflow.ReviewDecision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StageRetryBudgetFailsClosedAndCancellationStopsFutureStages()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-workflow-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await Fixture.CreateAsync(root, autoApprove: true, autoLaunch: true,
                inspector: new ThrowingInspector());
            var failed = await fixture.Workflows.CreateAsync(fixture.Job.Id, fixture.Target, fixture.Policy.Name,
                "fail", default);
            await fixture.Workflows.ProcessNextAsync(default); // Triggered -> Planned.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await fixture.Workflows.ProcessNextAsync(default);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(50));
            }
            failed = (await fixture.Workflows.GetAsync(failed.Id, default))!;
            Assert.Equal(AutomationWorkflowStatus.Failed, failed.Status);
            Assert.Equal("stage_retry_budget_exhausted", failed.EscalationReason);

            var cancelled = await fixture.Workflows.CreateAsync(fixture.Job.Id, fixture.Target, fixture.Policy.Name,
                "cancel", default);
            cancelled = (await fixture.Workflows.CancelAsync(cancelled.Id, default))!;
            Assert.Equal(AutomationWorkflowStatus.Cancelled, cancelled.Status);
            Assert.Null(await fixture.Workflows.ProcessNextAsync(default));
            Assert.Null(await fixture.Executions.GetAsync(cancelled.Id, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task PostgreSqlTriggerAndClaimAreAtomic()
    {
        await MigrationTests.InSchema(async source =>
        {
            var jobs = new PostgresJobStore(source);
            await jobs.InitializeAsync(default);
            var job = QualityJob.Create(new(new("stub", "workflow")), DateTimeOffset.UtcNow);
            await jobs.CreateAsync(job, default);
            var store = new PostgresAutomationWorkflowStore(source);
            var now = DateTimeOffset.UtcNow;
            AutomationWorkflow New() => new(Guid.NewGuid().ToString("N"), new string('a', 64), new string('b', 64),
                job.Id, "http://127.0.0.1:8000/", AutomationWorkflowStatus.Triggered, "test", "v1",
                new string('c', 64), "{}", "test", 0, now, now,
                [new(AutomationWorkflowStatus.Triggered, now, new string('d', 64))]);

            var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CreateOrGetAsync(New(), default)));
            Assert.Single(created.Select(item => item.Id).Distinct());
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(TimeSpan.FromMinutes(1), default)));
            Assert.Single(claims, item => item is not null);
        });
    }

    private sealed class Fixture
    {
        public required QualityJob Job { get; init; }
        public required ExecutionPolicy Policy { get; init; }
        public required string Target { get; init; }
        public required AutomationWorkflowService Workflows { get; init; }
        public required ExecutionRequestService Executions { get; init; }
        public required SavingExecutor Executor { get; init; }

        public static async Task<Fixture> CreateAsync(string root, bool autoApprove, bool autoLaunch,
            QualityJob? existingJob = null, IUiInspector? inspector = null)
        {
            var jobs = new FileJobStore(Path.Combine(root, "jobs"), TimeProvider.System);
            await jobs.InitializeAsync(default);
            var job = existingJob ?? CompletedJob();
            if (await jobs.GetAsync(job.Id, default) is null) await jobs.CreateAsync(job, default);
            var requests = new FileExecutionRequestStore(Path.Combine(root, "requests"));
            var runStore = new FileTestRunStore(Path.Combine(root, "runs"));
            var executor = new SavingExecutor(runStore);
            var policy = new ExecutionPolicy("workflow-test", "v1", ["http://127.0.0.1:8000"],
                ["goto", "expectText", "expectVisible"], NonProduction: true, AutoApprove: autoApprove,
                AutoLaunch: autoLaunch, CanaryMaxAutoLaunches: autoLaunch ? 5 : 0,
                MaxConcurrentAutoLaunches: 2, MaxAutoLaunchesPerWindow: 5);
            var catalog = new ExecutionPolicyCatalog([policy]);
            var executions = new ExecutionRequestService(requests, jobs, executor,
                new(root, ["http://127.0.0.1:8000"]), TimeProvider.System, policies: catalog);
            var workflowStore = new FileAutomationWorkflowStore(Path.Combine(root, "workflows"));
            var ui = inspector ?? new StaticInspector(new("http://127.0.0.1:8000/",
                [new("h1", "h1", "Quality", "h1") ]));
            return new() { Job = job, Policy = policy, Target = "http://127.0.0.1:8000/",
                Executions = executions, Executor = executor,
                Workflows = new(workflowStore, jobs, executions, ui, runStore, catalog, TimeProvider.System) };
        }
    }

    private static QualityJob CompletedJob()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new TestPlan("plan-workflow", "requirement-workflow", "Workflow",
            [new("TC-1", "requirement-workflow", "Page", "HappyPath", "P1", ["AC-1"],
                [new("Open", "Quality")])], [], [], "plan/v2", false);
        return new(Guid.NewGuid().ToString("N"), new("stub", "WORKFLOW-1"), JobStatus.Completed,
            now, now, 0, null, plan, [], [], null, null, null);
    }

    private sealed class StaticInspector(UiInspection result) : IUiInspector
    {
        public Task<UiInspection> InspectAsync(Uri target, CancellationToken ct) => Task.FromResult(result);
    }
    private sealed class ThrowingInspector : IUiInspector
    {
        public Task<UiInspection> InspectAsync(Uri target, CancellationToken ct) => throw new IOException("fixture");
    }
    private sealed class SavingExecutor(ITestRunStore store) : IReviewedTestExecutor
    {
        public int Calls { get; private set; }
        public Task<TestRun> ExecuteAsync(TestPlan plan, Uri baseUrl, CancellationToken ct) => throw new NotSupportedException();
        public async Task<TestRun> ExecuteReviewedAsync(TestPlan plan, byte[] manifestBytes, string reviewedSha256, CancellationToken ct)
        {
            Calls++;
            var now = DateTimeOffset.UtcNow;
            var run = new TestRun(Guid.NewGuid().ToString("N"), plan.Id, "Passed", now, now, [], "fixture",
                ManifestHash: reviewedSha256, FailureClassification: "None");
            await store.SaveAsync(run, ct);
            return run;
        }
    }
}
