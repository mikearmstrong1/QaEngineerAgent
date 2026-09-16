using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class JobCancellationTests
{
    private static readonly JobLeaseOptions Fast = new()
    {
        Duration = TimeSpan.FromSeconds(5), RenewalInterval = TimeSpan.FromSeconds(1),
        RenewalTimeout = TimeSpan.FromMilliseconds(500), CancellationPollInterval = TimeSpan.FromMilliseconds(25)
    };

    [Fact]
    public Task FileCancellationIsAtomicAndDurable() => WithFile(async store =>
    {
        await VerifyStore(store);
        await VerifyClaimCancelRace(store);
    });

    [PostgresFact]
    public Task PostgresCancellationIsAtomicAndStopsWorkers() => MigrationTests.InSchema(async source =>
    {
        await new PostgresJobStore(source).InitializeAsync(default);
        Func<IJobStore> store = () => new PostgresJobStore(source);
        await VerifyStore(store);
        await VerifyClaimCancelRace(store);
        await VerifyWorker(store, true, false);
        await VerifyWorker(store, false, true);
    });

    private static JobService Service(IJobStore store) => new(store, new StubRequirementSource(), new StubLlmProvider(), TimeProvider.System, Fast);

    private static async Task VerifyStore(Func<IJobStore> store)
    {
        var request = new JobRequest(new("stub", "cancel"));
        var key = Guid.NewGuid().ToString("N");
        var job = await Service(store()).SubmitAsync(request, default, key);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Service(store()).CancelAsync(job.Id, default)));
        Assert.All(results, result =>
        {
            Assert.Equal(JobStatus.Cancelled, result!.Status);
            Assert.Equal(job.Revision + 1, result.Revision);
            Assert.Single(result.Transitions, t => t.Status == JobStatus.Cancelled);
            Assert.True(result.IsTerminal);
            Assert.Equal("job_cancelled", result.Error);
            Assert.Null(result.LeaseToken);
            Assert.Null(result.LeaseUntil);
        });
        var replay = await Service(store()).SubmitAsync(request, default, key);
        Assert.Equal(job.Id, replay.Id);
        Assert.Equal(JobStatus.Cancelled, replay.Status);
        Assert.Null(await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default));
        Assert.Null(await store().ClaimAsync(null, TimeSpan.FromSeconds(5), default));
        Assert.Null(await Service(store()).CancelAsync(Guid.NewGuid().ToString("N"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(store()).CancelAsync("bad-id", default));

        job = await Service(store()).SubmitAsync(request, default);
        var owner = (await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default))!;
        var cancelled = (await store().CancelAsync(job.Id, default))!;
        Assert.Equal(owner.Revision + 1, cancelled.Revision);
        await Assert.ThrowsAsync<LeaseLostException>(() => store().SaveAsync(owner, default));
        await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(owner, TimeSpan.FromSeconds(5), default));
        Assert.Null(await Service(store()).ProcessNextAsync(job.Id, default));

        job = await Service(store()).SubmitAsync(request, default);
        var complete = (await Service(store()).ProcessNextAsync(job.Id, default))!;
        Assert.Equal(JobStatus.Completed, complete.Status);
        Assert.Equal(complete.Revision, (await store().CancelAsync(job.Id, default))!.Revision);
        Assert.Equal(JobStatus.Completed, (await store().GetAsync(job.Id, default))!.Status);

        // Both operations serialize through the same lock/row; only the first terminal commit wins.
        foreach (var status in new[] { JobStatus.Failed, JobStatus.Completed })
        {
            job = await Service(store()).SubmitAsync(request, default);
            owner = (await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default))!;
            var finishing = owner.TransitionTo(JobStatus.Normalizing, DateTimeOffset.UtcNow, "test");
            finishing = (finishing with { Requirement = complete.Requirement, TestPlan = complete.TestPlan })
                .TransitionTo(JobStatus.Planning, DateTimeOffset.UtcNow, "test")
                .TransitionTo(status, DateTimeOffset.UtcNow, "test terminal");
            async Task Finish() { try { await store().SaveAsync(finishing, default); } catch (LeaseLostException) { } }
            await Task.WhenAll(Finish(), store().CancelAsync(job.Id, default));
            var terminal = (await store().GetAsync(job.Id, default))!;
            Assert.Contains(terminal.Status, new[] { status, JobStatus.Cancelled });
            Assert.Single(terminal.Transitions, t => t.Status is JobStatus.Failed or JobStatus.Completed or JobStatus.Cancelled);
            Assert.Equal(terminal.Revision, (await store().CancelAsync(job.Id, default))!.Revision);
        }
    }

    private static async Task VerifyClaimCancelRace(Func<IJobStore> store)
    {
        var request = new JobRequest(new("stub", "claim-cancel-race"));
        var job = await Service(store()).SubmitAsync(request, default);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimTask = Task.Run(async () => { await start.Task; return await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default); });
        var cancelTask = Task.Run(async () => { await start.Task; return await store().CancelAsync(job.Id, default); });
        start.SetResult();
        await Task.WhenAll(claimTask, cancelTask);
        var cancelResult = (await cancelTask)!;
        Assert.Equal(JobStatus.Cancelled, cancelResult.Status);
        Assert.Null(cancelResult.LeaseToken);
        Assert.Null(cancelResult.LeaseUntil);
        var claimResult = await claimTask;
        if (claimResult is { } oldOwner)
        {
            await Assert.ThrowsAsync<LeaseLostException>(() => store().SaveAsync(oldOwner, default));
            await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(oldOwner, TimeSpan.FromSeconds(5), default));
        }
        else
        {
            Assert.Null(claimResult);
        }
        var fetched = (await store().GetAsync(job.Id, default))!;
        Assert.Equal(JobStatus.Cancelled, fetched.Status);
        Assert.Null(await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public Task AnotherInstanceCancelsAnActiveProvider(bool normalize, bool ignoreCancellation)
        => WithFile(store => VerifyWorker(store, normalize, ignoreCancellation));

    private static async Task VerifyWorker(Func<IJobStore> store, bool normalize, bool ignoreCancellation)
    {
        var provider = new PausedProvider(normalize, ignoreCancellation);
        var service = new JobService(store(), provider, provider, TimeProvider.System, Fast);
        var job = await service.SubmitAsync(new(new("stub", "active-cancel")), default);
        var processing = service.ProcessNextAsync(job.Id, default);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelled = (await Service(store()).CancelAsync(job.Id, default))!;
            var result = (await processing.WaitAsync(TimeSpan.FromSeconds(10)))!;
            Assert.Equal(JobStatus.Cancelled, result.Status);
            Assert.Equal(cancelled.Revision, result.Revision);
            Assert.True(provider.Token.IsCancellationRequested);
            var operation = result.ProviderOperations!.Single(o => o.Stage == (normalize ? "Normalize" : "Plan"));
            Assert.Equal(normalize ? ProviderOperationStatus.Cancelled : ProviderOperationStatus.OutcomeUnknown, operation.Status);
            Assert.Equal(normalize ? "job_cancelled" : "provider_operation_outcome_unknown", operation.Error);
            Assert.NotNull(operation.FinishedAt);
            Assert.Null(operation.RetryAt);
            provider.Release.TrySetResult();
            await provider.Exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100); // Let a provider that ignored its token attempt its late checkpoint.
            var persisted = (await store().GetAsync(job.Id, default))!;
            Assert.Equal(cancelled.Revision, persisted.Revision);
            Assert.Null(persisted.TestPlan);
            Assert.Equal(normalize ? 0 : 1, provider.Plans);
            Assert.Null(await Service(store()).ProcessNextAsync(job.Id, default));
        }
        finally { provider.Release.TrySetResult(); }
    }

    [Fact]
    public Task CancellationDuringBackoffPreventsAnotherRead() => WithFile(async store =>
    {
        var source = new FailingSource();
        var service = new JobService(store(), source, new StubLlmProvider(), TimeProvider.System, Fast,
            new RetryBudgetOptions { InitialDelayMilliseconds = 2000, MaxDelayMilliseconds = 2000 });
        var job = await service.SubmitAsync(new(new("stub", "backoff")), default);
        var processing = service.ProcessNextAsync(job.Id, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await store().GetAsync(job.Id, timeout.Token))!.ProviderOperations?.SingleOrDefault()?.RetryAt is null)
            await Task.Delay(10, timeout.Token);
        await store().CancelAsync(job.Id, default);
        Assert.Equal(JobStatus.Cancelled, (await processing.WaitAsync(timeout.Token))!.Status);
        Assert.Equal(1, source.Calls);
    });

    [Fact]
    public Task CancellationAfterPlanCheckpointPreservesResultAndFencesCompletion() => WithFile(async store =>
    {
        var wrapped = new InterceptingStore(store()) { CancelAfterPlan = true };
        var job = await Service(wrapped).SubmitAsync(new(new("stub", "checkpoint")), default);
        var result = (await Service(wrapped).ProcessNextAsync(job.Id, default))!;
        Assert.Equal(JobStatus.Cancelled, result.Status);
        Assert.NotNull(result.TestPlan);
        Assert.All(result.ProviderOperations!, operation => Assert.Equal(ProviderOperationStatus.Completed, operation.Status));
        Assert.DoesNotContain(result.Transitions, t => t.Status == JobStatus.Completed);
    });

    [Fact]
    public Task CancellationWinsAgainstDeadlineFailureAlreadyBeingSaved() => WithFile(async store =>
    {
        var wrapped = new InterceptingStore(store()) { CancelBeforeDeadlineFailure = true };
        var provider = new PausedProvider(false, false);
        var options = Fast with { CancellationPollInterval = TimeSpan.FromSeconds(2) };
        var service = new JobService(wrapped, provider, provider, TimeProvider.System, options,
            new RetryBudgetOptions(MaxDurationSeconds: 1));
        var job = await service.SubmitAsync(new(new("stub", "deadline-race")), default);
        var result = (await service.ProcessNextAsync(job.Id, default).WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Equal(JobStatus.Cancelled, result.Status);
        Assert.Equal("job_cancelled", result.Error);
        Assert.DoesNotContain(result.Transitions, t => t.Status == JobStatus.Failed);
    });

    [Fact]
    public Task MonitorFailureStopsProcessingWithoutClaimingCancellation() => WithFile(async store =>
    {
        var wrapped = new InterceptingStore(store()) { FailReads = true };
        var provider = new PausedProvider(false, false);
        var service = new JobService(wrapped, provider, provider, TimeProvider.System, Fast);
        var job = await service.SubmitAsync(new(new("stub", "monitor-failure")), default);
        await Assert.ThrowsAsync<JobMonitoringException>(() => service.ProcessNextAsync(job.Id, default).WaitAsync(TimeSpan.FromSeconds(10)));
        var saved = (await store().GetAsync(job.Id, default))!;
        Assert.False(saved.IsTerminal);
        Assert.Null(saved.Error);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void InvalidPollingIntervalIsRejected(int seconds)
        => Assert.Throws<ArgumentException>(() => (Fast with { CancellationPollInterval = TimeSpan.FromSeconds(seconds) }).Validate());

    private static async Task WithFile(Func<Func<IJobStore>, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cancellation-" + Guid.NewGuid().ToString("N"));
        try { await action(() => new FileJobStore(directory, TimeProvider.System)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FailingSource : IRequirementSource
    {
        public int Calls;
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
        { Calls++; throw new HttpRequestException("transient"); }
    }

    private sealed class PausedProvider(bool normalize, bool ignoreCancellation) : IRequirementSource, ILlmProvider
    {
        public string Name => "paused";
        public int Plans;
        public CancellationToken Token;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Pause(CancellationToken ct)
        {
            Token = ct;
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ignoreCancellation ? CancellationToken.None : ct); }
            finally { Exited.TrySetResult(); }
        }
        public async Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
        {
            if (normalize) await Pause(ct);
            return await new StubRequirementSource().NormalizeAsync(reference, default);
        }
        public async Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
        {
            Plans++;
            if (!normalize) await Pause(ct);
            return await new StubLlmProvider().PlanAsync(requirement, promptVersion, default);
        }
    }

    private sealed class InterceptingStore(IJobStore inner) : IJobStore
    {
        public bool CancelAfterPlan { get; init; }
        public bool FailReads { get; init; }
        public bool CancelBeforeDeadlineFailure { get; init; }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CreateAsync(QualityJob job, CancellationToken ct) => inner.CreateAsync(job, ct);
        public Task<QualityJob> CreateOrGetAsync(QualityJob job, CancellationToken ct) => inner.CreateOrGetAsync(job, ct);
        public Task<QualityJob?> GetAsync(string id, CancellationToken ct)
            => FailReads ? Task.FromException<QualityJob?>(new IOException("private details")) : inner.GetAsync(id, ct);
        public Task<QualityJob?> CancelAsync(string id, CancellationToken ct) => inner.CancelAsync(id, ct);
        public Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct) => inner.ClaimAsync(id, lease, ct);
        public Task<QualityJob> RenewLeaseAsync(QualityJob job, TimeSpan lease, CancellationToken ct) => inner.RenewLeaseAsync(job, lease, ct);
        public async Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
        {
            if (CancelBeforeDeadlineFailure && job.Error == "job_time_budget_exhausted") await inner.CancelAsync(job.Id, ct);
            var saved = await inner.SaveAsync(job, ct);
            if (CancelAfterPlan && saved.TestPlan is not null && !saved.IsTerminal) await inner.CancelAsync(job.Id, ct);
            return saved;
        }
    }
}
