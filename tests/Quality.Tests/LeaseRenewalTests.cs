using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class LeaseRenewalTests
{
    private static readonly JobLeaseOptions Fast = new()
    {
        Duration = TimeSpan.FromSeconds(3), RenewalInterval = TimeSpan.FromMilliseconds(100),
        RenewalTimeout = TimeSpan.FromMilliseconds(700)
    };

    [Fact]
    public Task FileRenewalFencesOwnershipAndPreservesCheckpoints() => WithFile(VerifyStore);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FileLongRunningProviderKeepsItsClaim(bool normalize)
        => WithFile(store => VerifyLongCall(store, normalize));

    [PostgresFact]
    public async Task PostgresRenewalAndLongRunningProviders()
    {
        await MigrationTests.InSchema(async source =>
        {
            await new PostgresJobStore(source).InitializeAsync(default);
            Func<IJobStore> store = () => new PostgresJobStore(source);
            await VerifyStore(store);
            await VerifyLongCall(store, true);
            await VerifyLongCall(store, false);
            await VerifyLockWait(source);
        });
    }

    private static async Task VerifyStore(Func<IJobStore> store)
    {
        var job = QualityJob.Create(new(new("stub", "renew")), DateTimeOffset.UtcNow);
        await store().CreateAsync(job, default);
        var claimed = (await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default))!;
        var renewed = await store().RenewLeaseAsync(claimed with { Error = "must-not-be-persisted" }, TimeSpan.FromSeconds(10), default);
        Assert.Null(renewed.Error);
        Assert.Equal(claimed.Revision + 1, renewed.Revision);
        Assert.Equal(claimed.LeaseToken, renewed.LeaseToken);
        Assert.True(renewed.LeaseUntil > claimed.LeaseUntil);
        Assert.Equal(renewed.LeaseUntil, (await store().GetAsync(job.Id, default))!.LeaseUntil);
        Assert.Null(await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default));
        await Assert.ThrowsAsync<LeaseLostException>(() => store().SaveAsync(claimed, default));
        await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(claimed, TimeSpan.FromSeconds(10), default));
        await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(renewed with { LeaseToken = "wrong-owner" }, TimeSpan.FromSeconds(10), default));
        var shorter = await store().RenewLeaseAsync(renewed, TimeSpan.FromSeconds(1), default);
        Assert.Equal(renewed.LeaseUntil, shorter.LeaseUntil);
        var expired = await store().SaveAsync(shorter with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
        await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(expired, TimeSpan.FromSeconds(10), default));
        var replacement = (await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(10), default))!;
        await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(expired, TimeSpan.FromSeconds(10), default));
        var done = await store().SaveAsync(replacement.TransitionTo(JobStatus.Failed, DateTimeOffset.UtcNow, "test"), default);
        await Assert.ThrowsAsync<LeaseLostException>(() => store().RenewLeaseAsync(done, TimeSpan.FromSeconds(10), default));
    }

    private static async Task VerifyLongCall(Func<IJobStore> store, bool normalize)
    {
        var provider = new PausedProvider(normalize);
        var tracked = new TrackingStore(store());
        var service = new JobService(tracked, provider, provider, TimeProvider.System, Fast);
        var job = await service.SubmitAsync(new(new("stub", "long-call")), default);
        var processing = service.ProcessNextAsync(job.Id, default);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await tracked.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var wait = tracked.OriginalLeaseUntil!.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            var running = (await store().GetAsync(job.Id, default))!;
            Assert.True(running.LeaseUntil > DateTimeOffset.UtcNow);
            Assert.True(running.LeaseUntil > tracked.OriginalLeaseUntil);
            Assert.Null(await store().ClaimAsync(job.Id, TimeSpan.FromSeconds(3), default));
        }
        finally { provider.Release.TrySetResult(); }
        var completed = (await processing.WaitAsync(TimeSpan.FromSeconds(15)))!;
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(1, provider.Reads);
        Assert.Equal(1, provider.Plans);
        Assert.True(tracked.Renewals > 1);
        Assert.False(tracked.OverlappedWrites);
        Assert.Null(completed.LeaseToken);
        Assert.Null(completed.LeaseUntil);
        var calls = tracked.Renewals;
        await Task.Delay(Fast.RenewalInterval * 2);
        Assert.Equal(calls, tracked.Renewals);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("ack-lost")]
    public Task RenewalFailureCancelsProviderAndLeavesRecoveryCheckpoint(string mode) => WithFile(async store =>
    {
        var provider = new PausedProvider(false);
        var tracked = new TrackingStore(store()) { Failure = mode };
        var service = new JobService(tracked, provider, provider, TimeProvider.System, Fast);
        var job = await service.SubmitAsync(new(new("stub", "renewal-failure")), default);
        var processing = service.ProcessNextAsync(job.Id, default);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (mode == "lost") await Assert.ThrowsAsync<LeaseLostException>(() => processing.WaitAsync(TimeSpan.FromSeconds(15)));
        else await Assert.ThrowsAsync<LeaseRenewalException>(() => processing.WaitAsync(TimeSpan.FromSeconds(15)));
        await provider.Exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var persisted = (await store().GetAsync(job.Id, default))!;
        Assert.Equal(JobStatus.Planning, persisted.Status);
        Assert.Null(persisted.Error);
        Assert.Null(persisted.TestPlan);
        Assert.Equal(ProviderOperationStatus.Started, persisted.ProviderOperations!.Single(o => o.Stage == "Plan").Status);
        var calls = tracked.Renewals;
        await Task.Delay(Fast.RenewalInterval * 2);
        Assert.Equal(calls, tracked.Renewals);
        await store().SaveAsync(persisted with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
        var recovered = await new JobService(store(), new StubRequirementSource(), new StubLlmProvider(), TimeProvider.System)
            .ProcessNextAsync(job.Id, default);
        Assert.Equal("provider_operation_outcome_unknown", recovered!.Error);
        Assert.Equal(1, provider.Plans);
    });

    [Fact]
    public Task CancellationStopsRenewalEvenWhenProviderIgnoresCancellation() => WithFile(async store =>
    {
        using var cancel = new CancellationTokenSource();
        var provider = new PausedProvider(false, ignoreCancellation: true);
        var tracked = new TrackingStore(store());
        var service = new JobService(tracked, provider, provider, TimeProvider.System, Fast);
        var job = await service.SubmitAsync(new(new("stub", "ignore-cancellation")), default);
        var processing = service.ProcessNextAsync(job.Id, cancel.Token);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await tracked.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(15)));
            var before = (await store().GetAsync(job.Id, default))!;
            var renewals = tracked.Renewals;
            provider.Release.TrySetResult();
            await provider.Exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(Fast.RenewalInterval * 2);
            var after = (await store().GetAsync(job.Id, default))!;
            Assert.Equal(before.Revision, after.Revision);
            Assert.Null(after.TestPlan);
            Assert.Equal(renewals, tracked.Renewals);
        }
        finally { provider.Release.TrySetResult(); }
    });

    [Fact]
    public Task BlockedCheckpointCannotPreventRenewalTimeout() => WithFile(async store =>
    {
        var tracked = new TrackingStore(store()) { BlockPlanCheckpoint = true };
        var service = new JobService(tracked, new StubRequirementSource(), new StubLlmProvider(), TimeProvider.System, Fast);
        var job = await service.SubmitAsync(new(new("stub", "blocked-save")), default);
        await Assert.ThrowsAsync<LeaseRenewalException>(() => service.ProcessNextAsync(job.Id, default).WaitAsync(TimeSpan.FromSeconds(15)));
        var persisted = (await store().GetAsync(job.Id, default))!;
        Assert.Null(persisted.TestPlan);
        Assert.Equal(JobStatus.Planning, persisted.Status);
    });

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(5, 0, 1)]
    [InlineData(5, 1, 0)]
    [InlineData(5, 3, 2)]
    [InlineData(86401, 1, 1)]
    public void InvalidRenewalSettingsFailBeforeWork(int duration, int interval, int timeout)
        => Assert.Throws<ArgumentException>(() => new JobLeaseOptions
        {
            Duration = TimeSpan.FromSeconds(duration), RenewalInterval = TimeSpan.FromSeconds(interval),
            RenewalTimeout = TimeSpan.FromSeconds(timeout)
        }.Validate());

    private static async Task VerifyLockWait(NpgsqlDataSource source)
    {
        var store = new PostgresJobStore(source);
        var job = QualityJob.Create(new(new("stub", "lock-wait")), DateTimeOffset.UtcNow);
        await store.CreateAsync(job, default);
        var claimed = (await store.ClaimAsync(job.Id, TimeSpan.FromMilliseconds(500), default))!;
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM quality_jobs WHERE id=$1 FOR UPDATE";
        command.Parameters.AddWithValue(job.Id);
        await command.ExecuteScalarAsync();
        var renewal = store.RenewLeaseAsync(claimed, TimeSpan.FromSeconds(10), default);
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        await transaction.CommitAsync();
        await Assert.ThrowsAsync<LeaseLostException>(() => renewal);
    }

    private static async Task WithFile(Func<Func<IJobStore>, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lease-tests-" + Guid.NewGuid().ToString("N"));
        try { await action(() => new FileJobStore(directory, TimeProvider.System)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class PausedProvider(bool normalize, bool ignoreCancellation = false) : IRequirementSource, ILlmProvider
    {
        public string Name => "paused";
        public int Reads, Plans;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Pause(CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ignoreCancellation ? CancellationToken.None : ct); }
            finally { Exited.TrySetResult(); }
        }
        public async Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
        {
            Reads++;
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

    private sealed class TrackingStore(IJobStore inner) : IJobStore
    {
        public string? Failure { get; init; }
        public bool BlockPlanCheckpoint { get; init; }
        public int Renewals;
        public bool OverlappedWrites;
        private int writes;
        public DateTimeOffset? OriginalLeaseUntil;
        public TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CreateAsync(QualityJob job, CancellationToken ct) => inner.CreateAsync(job, ct);
        public Task<QualityJob> CreateOrGetAsync(QualityJob job, CancellationToken ct) => inner.CreateOrGetAsync(job, ct);
        public Task<QualityJob?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<QualityJob?> CancelAsync(string id, CancellationToken ct) => inner.CancelAsync(id, ct);
        public async Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct)
        {
            var job = await inner.ClaimAsync(id, lease, ct);
            OriginalLeaseUntil = job?.LeaseUntil;
            return job;
        }
        public async Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
        {
            if (Interlocked.Increment(ref writes) != 1) OverlappedWrites = true;
            try
            {
                if (BlockPlanCheckpoint && job.TestPlan is not null) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return await inner.SaveAsync(job, ct);
            }
            finally { Interlocked.Decrement(ref writes); }
        }
        public async Task<QualityJob> RenewLeaseAsync(QualityJob job, TimeSpan lease, CancellationToken ct)
        {
            if (Interlocked.Increment(ref writes) != 1) OverlappedWrites = true;
            Interlocked.Increment(ref Renewals);
            try
            {
                var failure = job.Status == JobStatus.Planning && job.ProviderOperations?.Any(o => o.Stage == "Plan") == true
                    ? Failure : null;
                if (failure == "lost") throw new LeaseLostException();
                if (failure == "error") throw new IOException("private connection details");
                if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                var result = await inner.RenewLeaseAsync(job, lease, ct);
                if (failure == "ack-lost") throw new IOException("Renewal committed; acknowledgement lost");
                Renewed.TrySetResult();
                return result;
            }
            finally { Interlocked.Decrement(ref writes); }
        }
    }
}
