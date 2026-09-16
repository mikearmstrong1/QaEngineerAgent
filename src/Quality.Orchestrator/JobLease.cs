using Quality.Domain;
namespace Quality.Orchestrator;

public sealed record JobLeaseOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan RenewalTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CancellationPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public void Validate()
    {
        if (CancellationPollInterval <= TimeSpan.Zero || CancellationPollInterval > TimeSpan.FromSeconds(60))
            throw new ArgumentException("Cancellation poll interval must be positive and at most 60 seconds");
        if (Duration <= TimeSpan.Zero || Duration > TimeSpan.FromDays(1) ||
            RenewalInterval <= TimeSpan.Zero || RenewalTimeout <= TimeSpan.Zero ||
            RenewalInterval >= Duration || RenewalTimeout >= Duration - RenewalInterval)
            throw new ArgumentException("Lease duration must be positive and at most one day; renewal interval plus timeout must be shorter than the lease");
    }
}

// One instance per claimed attempt. Checkpoint writes and renewal share a gate so a
// heartbeat revision cannot invalidate the worker's next checkpoint or be overwritten.
internal sealed class JobLease : IAsyncDisposable
{
    private readonly IJobStore store;
    private readonly JobLeaseOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource processing;
    private readonly CancellationTokenSource budgetDeadline;
    private readonly CancellationTokenSource work;
    public bool BudgetExpired => budgetDeadline.IsCancellationRequested && !processing.IsCancellationRequested;
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenSource renewal;
    private readonly Task heartbeat;
    private readonly Task cancellationMonitor;
    private volatile QualityJob? cancelledJob;
    public QualityJob? CancelledJob => cancelledJob;
    private QualityJob current;
    private volatile Exception? failure;
    public Exception? Failure => failure;
    public CancellationToken Token { get; }

    public JobLease(IJobStore store, QualityJob claimed, JobLeaseOptions options, TimeProvider clock, CancellationToken ct, TimeSpan remainingBudget)
    {
        this.store = store;
        this.options = options;
        this.clock = clock;
        current = claimed;
        processing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetDeadline = new CancellationTokenSource(remainingBudget > TimeSpan.Zero ? remainingBudget : Timeout.InfiniteTimeSpan, clock);
        if (remainingBudget <= TimeSpan.Zero) budgetDeadline.Cancel();
        work = CancellationTokenSource.CreateLinkedTokenSource(processing.Token, budgetDeadline.Token);
        Token = work.Token;
        renewal = CancellationTokenSource.CreateLinkedTokenSource(processing.Token, stopping.Token);
        heartbeat = RenewAsync();
        cancellationMonitor = MonitorCancellationAsync();
    }

    public async Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
    {
        Token.ThrowIfCancellationRequested();
        await gate.WaitAsync(Token);
        try
        {
            Token.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
            if (job.Id != current.Id || job.LeaseToken != current.LeaseToken || current.IsTerminal)
                throw new LeaseLostException();
            // The supplied document contains the next checkpoint; ownership metadata comes
            // from the latest successful renewal, never from the older provider-call snapshot.
            current = await store.SaveAsync(job with { Revision = current.Revision,
                LeaseToken = current.LeaseToken, LeaseUntil = current.LeaseUntil }, ct);
            if (current.IsTerminal) stopping.Cancel();
            return current;
        }
        finally { gate.Release(); }
    }

    public async Task<QualityJob> ExhaustTimeBudgetAsync()
    {
        await gate.WaitAsync(processing.Token);
        try
        {
            processing.Token.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
            if (current.IsTerminal) return current;
            current = await store.SaveAsync(current.ExhaustBudget("job_time_budget_exhausted", clock.GetUtcNow()), processing.Token);
            stopping.Cancel();
            return current;
        }
        finally { gate.Release(); }
    }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.RenewalInterval, clock, renewal.Token);
                using var deadline = new CancellationTokenSource(options.RenewalTimeout, clock);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(renewal.Token, deadline.Token);
                await gate.WaitAsync(attempt.Token);
                try
                {
                    if (current.IsTerminal) return;
                    var pending = store.RenewLeaseAsync(current, options.Duration, attempt.Token);
                    try { current = await pending.WaitAsync(attempt.Token); }
                    finally { Observe(pending); }
                }
                catch (Exception ex) when (!renewal.IsCancellationRequested)
                {
                    // Publish failure before releasing the gate: a waiting checkpoint must
                    // not slip through between a failed renewal and cancellation delivery.
                    failure = ex is LeaseLostException ? ex : new LeaseRenewalException();
                    throw;
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex is LeaseLostException ? ex : new LeaseRenewalException();
            await processing.CancelAsync();
        }
    }

    private async Task MonitorCancellationAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.CancellationPollInterval, clock, renewal.Token);
                using var deadline = new CancellationTokenSource(options.RenewalTimeout, clock);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(renewal.Token, deadline.Token);
                var pending = store.GetAsync(current.Id, attempt.Token);
                QualityJob? snapshot;
                try { snapshot = await pending.WaitAsync(attempt.Token); }
                finally { Observe(pending); }
                if (snapshot?.Status == JobStatus.Cancelled)
                {
                    cancelledJob = snapshot;
                    await processing.CancelAsync();
                    return;
                }
                if (snapshot is null) throw new LeaseLostException();
            }
        }
        catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex is LeaseLostException ? ex : new JobMonitoringException();
            await processing.CancelAsync();
        }
    }

    // A provider or store may finish after cancellation. Observe its exception without
    // waiting indefinitely; all later checkpoint writes still check the cancelled token.
    internal static void Observe(Task task)
        => _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await Task.WhenAll(heartbeat, cancellationMonitor);
        await processing.CancelAsync();
        work.Dispose();
        budgetDeadline.Dispose();
        renewal.Dispose();
        stopping.Dispose();
        processing.Dispose();
        // Do not dispose the gate: a cancelled in-flight checkpoint may still release it.
    }
}

public sealed class LeaseRenewalException() : Exception("Lease renewal failed or timed out; processing stopped");

public sealed class JobMonitoringException() : Exception("Job cancellation check failed or timed out; processing stopped");
