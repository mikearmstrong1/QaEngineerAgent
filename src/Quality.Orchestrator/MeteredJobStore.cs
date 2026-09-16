using Quality.Domain;
namespace Quality.Orchestrator;

// Measures acknowledgements; a committed write whose response is lost is an error observation.
public sealed class MeteredJobStore(IJobStore inner, JobMetrics metrics) : IJobStore
{
    public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
    public async Task CreateAsync(QualityJob job, CancellationToken ct)
        => await metrics.TrackAsync(MetricOperation.Submit, async () => { await inner.CreateAsync(job, ct); return true; });
    public Task<QualityJob> CreateOrGetAsync(QualityJob job, CancellationToken ct)
        => metrics.TrackAsync(MetricOperation.Submit, () => inner.CreateOrGetAsync(job, ct),
            result => result.Reference != job.Reference ? MetricOutcome.Conflict : result.Id == job.Id ? MetricOutcome.Success : MetricOutcome.Replayed);
    public Task<QualityJob?> GetAsync(string id, CancellationToken ct)
        => metrics.TrackAsync(MetricOperation.Read, () => inner.GetAsync(id, ct), result => result is null ? MetricOutcome.Missing : MetricOutcome.Success);
    public Task<QualityJob?> CancelAsync(string id, CancellationToken ct)
        => metrics.TrackAsync(MetricOperation.Cancel, () => inner.CancelAsync(id, ct),
            result => result is null ? MetricOutcome.Missing : result.Status == JobStatus.Cancelled ? MetricOutcome.Cancelled : MetricOutcome.Conflict);
    public Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct)
        => metrics.TrackAsync(MetricOperation.Claim, () => inner.ClaimAsync(id, lease, ct),
            result => result is null ? MetricOutcome.Idle : result.IsTerminal ? TerminalOutcome(result) : MetricOutcome.Success);
    public Task<QualityJob> RenewLeaseAsync(QualityJob job, TimeSpan lease, CancellationToken ct)
        => metrics.TrackAsync(MetricOperation.Renew, () => inner.RenewLeaseAsync(job, lease, ct));
    public Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
        => metrics.TrackAsync(MetricOperation.Save, () => inner.SaveAsync(job, ct), TerminalOutcome);
    public static MetricOutcome TerminalOutcome(QualityJob job) => job.Status switch
    {
        JobStatus.Completed => MetricOutcome.Completed,
        JobStatus.Cancelled => MetricOutcome.Cancelled,
        JobStatus.Failed when job.Error is "job_time_budget_exhausted" or "job_retry_budget_exhausted" or "normalization_retry_budget_exhausted" => MetricOutcome.BudgetExhausted,
        JobStatus.Failed => MetricOutcome.Failed,
        _ => MetricOutcome.Success
    };
}
