using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class ProviderRecoveryTests
{
    public static TheoryData<string, bool, int, int, bool> CrashCases => new()
    {
        // checkpoint, committed before interruption, source calls, planning calls, needs review
        { "normalize-start", false, 1, 1, false },
        { "normalize-start", true, 1, 1, false },
        { "normalize-result", false, 2, 1, false },
        { "normalize-result", true, 1, 1, false },
        { "plan-start", false, 1, 1, false },
        { "plan-start", true, 1, 0, true },
        { "plan-result", false, 1, 1, true },
        { "plan-result", true, 1, 1, false },
        { "terminal", false, 1, 1, false },
        { "terminal", true, 1, 1, false },
    };

    [Theory]
    [MemberData(nameof(CrashCases))]
    public Task FileRecoveryAtEveryProviderBoundary(string checkpoint, bool committed, int reads, int plans, bool review)
        => WithFile(store => VerifyCrash(store, checkpoint, committed, reads, plans, review));

    [PostgresFact]
    public async Task PostgresRecoveryAtEveryProviderBoundary()
    {
        await MigrationTests.InSchema(async source =>
        {
            await new PostgresJobStore(source).InitializeAsync(default);
            foreach (var row in CrashCases)
                await VerifyCrash(() => new PostgresJobStore(source), (string)row[0], (bool)row[1],
                    (int)row[2], (int)row[3], (bool)row[4]);
            await VerifyLateResult(() => new PostgresJobStore(source));
        });
    }

    private static async Task VerifyCrash(Func<IJobStore> store, string checkpoint, bool committed, int reads, int plans, bool review)
    {
        var source = new CountingSource();
        var planner = new CountingPlanner();
        var fault = new InterruptingStore(store(), checkpoint, committed);
        var service = new JobService(fault, source, planner, TimeProvider.System);
        var submitted = await service.SubmitAsync(new(new("stub", "recovery")), default, Guid.NewGuid().ToString("N"));
        // Simulates loss of the process before a commit or loss of the commit acknowledgement.
        await Assert.ThrowsAsync<IOException>(() => service.ProcessNextAsync(submitted.Id, default));
        var interrupted = (await store().GetAsync(submitted.Id, default))!;
        Assert.Null(interrupted.Error); // Storage failure must not become a provider failure.
        var operationIds = (interrupted.ProviderOperations ?? []).ToDictionary(o => o.Stage, o => o.Id);
        await Expire(store(), interrupted);
        var freshPlanner = new CountingPlanner { Name = "replacement-planner" };
        var recoveredService = new JobService(store(), source, freshPlanner, TimeProvider.System);
        var recovered = await recoveredService.ProcessNextAsync(submitted.Id, default);
        var result = (await store().GetAsync(submitted.Id, default))!;
        if (interrupted.IsTerminal) Assert.Null(recovered);
        Assert.Equal(reads, source.Calls);
        Assert.Equal(plans, planner.Calls + freshPlanner.Calls);
        Assert.Equal(review ? JobStatus.Failed : JobStatus.Completed, result.Status);
        Assert.Null(await recoveredService.ProcessNextAsync(result.Id, default));
        Assert.Equal(2, result.ProviderOperations!.Length);
        foreach (var operation in result.ProviderOperations)
        {
            if (operationIds.TryGetValue(operation.Stage, out var oldId)) Assert.Equal(oldId, operation.Id);
            Assert.NotNull(operation.FinishedAt);
            Assert.Equal(64, operation.InputHash.Length);
        }
        Assert.Equal(checkpoint == "normalize-start" && committed || checkpoint == "normalize-result" && !committed ? 2 : 1,
            result.ProviderOperations.Single(o => o.Stage == "Normalize").Attempts);
        Assert.Single(result.Decisions, d => d.Stage == "Normalize");
        Assert.Single(result.Decisions, d => d.Stage == "Plan");
        var planOperation = result.ProviderOperations.Single(o => o.Stage == "Plan");
        if (review)
        {
            Assert.Equal("provider_operation_outcome_unknown", result.Error);
            Assert.Equal(ProviderOperationStatus.OutcomeUnknown, planOperation.Status);
            Assert.Null(result.TestPlan);
        }
        else
        {
            Assert.Equal(ProviderOperationStatus.Completed, planOperation.Status);
            Assert.Equal([JobStatus.Queued, JobStatus.Normalizing, JobStatus.Planning, JobStatus.Completed],
                result.Transitions.Select(t => t.Status));
            if (interrupted.TestPlan is not null) Assert.Equal(interrupted.TestPlan.Id, result.TestPlan!.Id);
        }
        if (!interrupted.IsTerminal)
            await Assert.ThrowsAsync<LeaseLostException>(() => store().SaveAsync(interrupted, default));
    }

    [Fact]
    public Task PlanningCancellationLeavesAnUnknownOutcomeWithoutASecondCall() => WithFile(async store =>
    {
        using var cancel = new CancellationTokenSource();
        var planner = new CancellingPlanner(cancel);
        var service = new JobService(store(), new CountingSource(), planner, TimeProvider.System);
        var job = await service.SubmitAsync(new(new("stub", "cancel-plan")), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessNextAsync(job.Id, cancel.Token));
        await Expire(store(), (await store().GetAsync(job.Id, default))!);
        var replacement = new CountingPlanner();
        var result = await new JobService(store(), new CountingSource(), replacement, TimeProvider.System).ProcessNextAsync(job.Id, default);
        Assert.Equal("provider_operation_outcome_unknown", result!.Error);
        Assert.Equal(0, replacement.Calls);
    });

    [Fact]
    public Task ChangedInputCannotReuseACheckpoint() => WithFile(async store =>
    {
        var planner = new CountingPlanner();
        var service = new JobService(new InterruptingStore(store(), "plan-result", true), new CountingSource(), planner, TimeProvider.System);
        var job = await service.SubmitAsync(new(new("stub", "changed")), default);
        await Assert.ThrowsAsync<IOException>(() => service.ProcessNextAsync(job.Id, default));
        var saved = (await store().GetAsync(job.Id, default))!;
        saved = await store().SaveAsync(saved with { Requirement = saved.Requirement! with { Title = "Changed input" } }, default);
        await Expire(store(), saved);
        var result = await new JobService(store(), new CountingSource(), planner, TimeProvider.System).ProcessNextAsync(job.Id, default);
        Assert.Equal("provider_operation_input_mismatch", result!.Error);
        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal(1, planner.Calls);
    });

    [Fact]
    public Task ReclaimedOperationRejectsLateResultFromOldWorker() => WithFile(VerifyLateResult);

    private static async Task VerifyLateResult(Func<IJobStore> store)
    {
        var planner = new PausedPlanner();
        var service = new JobService(store(), new CountingSource(), planner, TimeProvider.System);
        var job = await service.SubmitAsync(new(new("stub", "late-result")), default);
        var oldWorker = service.ProcessNextAsync(job.Id, default);
        try
        {
            await planner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expire(store(), (await store().GetAsync(job.Id, default))!);
            var replacement = new CountingPlanner();
            var recovered = await new JobService(store(), new CountingSource(), replacement, TimeProvider.System)
                .ProcessNextAsync(job.Id, default);
            Assert.Equal("provider_operation_outcome_unknown", recovered!.Error);
            Assert.Equal(0, replacement.Calls);
        }
        finally { planner.Release.TrySetResult(); }
        await Assert.ThrowsAsync<LeaseLostException>(() => oldWorker);
        var persisted = (await store().GetAsync(job.Id, default))!;
        Assert.Equal(JobStatus.Failed, persisted.Status);
        Assert.Null(persisted.TestPlan);
    }

    private static async Task Expire(IJobStore store, QualityJob job)
    {
        if (!job.IsTerminal)
            await store.SaveAsync(job with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
    }

    private static async Task WithFile(Func<Func<IJobStore>, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "provider-recovery-" + Guid.NewGuid().ToString("N"));
        try { await action(() => new FileJobStore(directory, TimeProvider.System)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class CountingSource : IRequirementSource
    {
        public int Calls;
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return new StubRequirementSource().NormalizeAsync(reference, ct);
        }
    }
    private sealed class CountingPlanner : ILlmProvider
    {
        public string Name { get; init; } = "counting-planner";
        public int Calls;
        public Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return new StubLlmProvider().PlanAsync(requirement, promptVersion, ct);
        }
    }
    private sealed class PausedPlanner : ILlmProvider
    {
        public string Name => "paused";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return await new StubLlmProvider().PlanAsync(requirement, promptVersion, ct);
        }
    }
    private sealed class CancellingPlanner(CancellationTokenSource cancel) : ILlmProvider
    {
        public string Name => "cancelling";
        public Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
        {
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }
    private sealed class InterruptingStore(IJobStore inner, string checkpoint, bool committed) : IJobStore
    {
        private bool interrupted;
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CreateAsync(QualityJob job, CancellationToken ct) => inner.CreateAsync(job, ct);
        public Task<QualityJob> CreateOrGetAsync(QualityJob job, CancellationToken ct) => inner.CreateOrGetAsync(job, ct);
        public Task<QualityJob?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<QualityJob?> CancelAsync(string id, CancellationToken ct) => inner.CancelAsync(id, ct);
        public Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct) => inner.ClaimAsync(id, lease, ct);
        public Task<QualityJob> RenewLeaseAsync(QualityJob job, TimeSpan lease, CancellationToken ct) => inner.RenewLeaseAsync(job, lease, ct);
        public async Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
        {
            var matches = checkpoint switch
            {
                "normalize-start" => Has(job, "Normalize", ProviderOperationStatus.Started),
                "normalize-result" => Has(job, "Normalize", ProviderOperationStatus.Completed) && job.Status == JobStatus.Normalizing,
                "plan-start" => Has(job, "Plan", ProviderOperationStatus.Started),
                "plan-result" => Has(job, "Plan", ProviderOperationStatus.Completed) && job.Status == JobStatus.Planning,
                "terminal" => job.Status == JobStatus.Completed,
                _ => false
            };
            if (!interrupted && matches)
            {
                interrupted = true;
                if (committed) await inner.SaveAsync(job, ct);
                throw new IOException("Simulated process interruption");
            }
            return await inner.SaveAsync(job, ct);
        }
        private static bool Has(QualityJob job, string stage, ProviderOperationStatus state)
            => job.ProviderOperations?.Any(o => o.Stage == stage && o.Status == state) ?? false;
    }
}
