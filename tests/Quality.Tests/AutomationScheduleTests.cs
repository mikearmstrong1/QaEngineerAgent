using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;

namespace Quality.Tests;

public sealed class AutomationScheduleTests
{
    [Fact]
    public async Task DueOccurrenceCreatesOneIdempotentWorkflowAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-schedule-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(now);
            var fixture = await CreateFixtureAsync(root, clock);
            var schedule = await fixture.Service.CreateAsync("nightly smoke", fixture.Job.Id, fixture.Target,
                fixture.Policy.Name, 300, now.AddMinutes(-20), true, default);

            var attempts = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => fixture.Service.ProcessNextDueAsync(default)));
            Assert.Single(attempts, item => item is not null);
            schedule = (await fixture.Service.GetAsync(schedule.Id, default))!;
            Assert.Equal(now.AddMinutes(-20), schedule.LastOccurrenceAt);
            Assert.Equal(now.AddMinutes(5), schedule.NextOccurrenceAt);
            Assert.NotNull(schedule.LastWorkflowId);
            Assert.Single(await fixture.Workflows.ListByJobAsync(fixture.Job.Id, default));

            fixture = await CreateFixtureAsync(root, clock, fixture.Job);
            var recovered = await fixture.Service.GetAsync(schedule.Id, default);
            Assert.Equal(schedule.NextOccurrenceAt, recovered!.NextOccurrenceAt);
            Assert.Equal(schedule.LastWorkflowId, recovered.LastWorkflowId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisabledScheduleKeepsDefinitionAndWorkflowHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-schedule-disabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(now);
            var fixture = await CreateFixtureAsync(root, clock);
            var schedule = await fixture.Service.CreateAsync("disabled smoke", fixture.Job.Id, fixture.Target,
                fixture.Policy.Name, 60, now, true, default);
            await fixture.Service.ProcessNextDueAsync(default);
            schedule = (await fixture.Service.GetAsync(schedule.Id, default))!;
            schedule = await fixture.Service.SetEnabledAsync(schedule.Id, schedule.Revision, false, default);
            clock.Now = now.AddHours(1);

            Assert.Null(await fixture.Service.ProcessNextDueAsync(default));
            Assert.False((await fixture.Service.GetAsync(schedule.Id, default))!.Enabled);
            Assert.Single(await fixture.Workflows.ListByJobAsync(fixture.Job.Id, default));
            await Assert.ThrowsAsync<AutomationScheduleConflictException>(() =>
                fixture.Service.SetEnabledAsync(schedule.Id, schedule.Revision - 1, true, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScheduleValidationRejectsUnsafeOrUnboundedDefinitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-schedule-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await CreateFixtureAsync(root, new MutableTimeProvider(DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync("bad", fixture.Job.Id,
                "https://production.example/", fixture.Policy.Name, 300, null, true, default));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync("too fast", fixture.Job.Id,
                fixture.Target, fixture.Policy.Name, 1, null, true, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task PostgreSqlDueClaimIsAtomic()
    {
        await MigrationTests.InSchema(async source =>
        {
            var jobs = new PostgresJobStore(source);
            await jobs.InitializeAsync(default);
            var store = new PostgresAutomationScheduleStore(source);
            var now = DateTimeOffset.UtcNow;
            var item = new AutomationSchedule(Guid.NewGuid().ToString("N"), "atomic schedule",
                Guid.NewGuid().ToString("N"), "http://127.0.0.1:8000/", "test", 60, true, now, now, now);
            await store.CreateAsync(item, default);

            var claims = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => store.ClaimDueAsync(now, TimeSpan.FromMinutes(1), default)));
            Assert.Single(claims, claim => claim is not null);
        });
    }

    private static async Task<Fixture> CreateFixtureAsync(string root, MutableTimeProvider clock,
        QualityJob? existing = null)
    {
        var jobs = new FileJobStore(Path.Combine(root, "jobs"), clock);
        await jobs.InitializeAsync(default);
        var job = existing ?? CompletedJob(clock.Now);
        if (await jobs.GetAsync(job.Id, default) is null) await jobs.CreateAsync(job, default);
        var policy = new ExecutionPolicy("schedule-test", "v1", ["http://127.0.0.1:8000"],
            ["goto", "expectText", "expectVisible"], NonProduction: true);
        var catalog = new ExecutionPolicyCatalog([policy]);
        var runStore = new FileTestRunStore(Path.Combine(root, "runs"));
        var executions = new ExecutionRequestService(new FileExecutionRequestStore(Path.Combine(root, "requests")),
            jobs, new NeverExecutor(), new(root, ["http://127.0.0.1:8000"]), clock, policies: catalog);
        var workflowStore = new FileAutomationWorkflowStore(Path.Combine(root, "workflows"));
        var workflowService = new AutomationWorkflowService(workflowStore, jobs, executions,
            new NeverInspector(), runStore, catalog, clock);
        var scheduleStore = new FileAutomationScheduleStore(Path.Combine(root, "schedules"));
        await scheduleStore.InitializeAsync(default);
        return new(job, policy, "http://127.0.0.1:8000/", workflowService,
            new AutomationScheduleService(scheduleStore, workflowService, jobs, catalog, clock));
    }

    private static QualityJob CompletedJob(DateTimeOffset now)
    {
        var plan = new TestPlan("plan-schedule", "requirement-schedule", "Schedule",
            [new("TC-1", "requirement-schedule", "Page", "HappyPath", "P1", ["AC-1"],
                [new("Open", "Quality")])], [], [], "plan/v2", false);
        return new(Guid.NewGuid().ToString("N"), new("stub", "SCHEDULE-1"), JobStatus.Completed,
            now, now, 0, null, plan, [], [], null, null, null);
    }

    private sealed record Fixture(QualityJob Job, ExecutionPolicy Policy, string Target,
        AutomationWorkflowService Workflows, AutomationScheduleService Service);
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NeverInspector : IUiInspector
    {
        public Task<UiInspection> InspectAsync(Uri target, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class NeverExecutor : IReviewedTestExecutor
    {
        public Task<TestRun> ExecuteAsync(TestPlan plan, Uri baseUrl, CancellationToken ct) => throw new NotSupportedException();
        public Task<TestRun> ExecuteReviewedAsync(TestPlan plan, byte[] manifestBytes, string reviewedSha256,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
