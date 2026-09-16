using Quality.Domain;
using System.Security.Cryptography;
using System.Text;
namespace Quality.Orchestrator;

public sealed class JobService(IJobStore store, IRequirementSource source, ILlmProvider llm, TimeProvider clock, JobLeaseOptions? leaseOptions = null, RetryBudgetOptions? retryOptions = null, JobMetrics? metrics = null)
{
    public const string PromptVersion = PlanningPrompt.Version;
    public Task<QualityJob?> GetAsync(string id, CancellationToken ct) => store.GetAsync(id, ct);
    public Task<QualityJob?> CancelAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid job id");
        return store.CancelAsync(id, ct);
    }
    public async Task<QualityJob> SubmitAsync(JobRequest request, CancellationToken ct, string? idempotencyKey = null)
    {
        var job = QualityJob.Create(request, clock.GetUtcNow()) with { RetryBudget = (retryOptions ?? new RetryBudgetOptions()).Snapshot() };
        if (idempotencyKey is not null)
        {
            if (idempotencyKey.Length is < 1 or > 200 || idempotencyKey.Any(c => c < 33 || c > 126))
                throw new ArgumentException("Idempotency-Key must contain 1-200 printable ASCII characters without spaces");
            job = job with { SubmissionKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))) };
            var existing = await store.CreateOrGetAsync(job, ct);
            if (existing.Reference != job.Reference) throw new IdempotencyConflictException();
            return existing;
        }
        await store.CreateAsync(job, ct);
        return job;
    }
    public async Task<QualityJob?> ProcessNextAsync(string? id, CancellationToken ct)
    {
        var settings = leaseOptions ?? new JobLeaseOptions();
        settings.Validate();
        var claimingAt = clock.GetTimestamp();
        var job = await store.ClaimAsync(id, settings.Duration, ct);
        if (job is null || job.IsTerminal) return job;
        // Store timestamps share one clock (PostgreSQL uses database time). Subtract the
        // full claim round trip conservatively rather than comparing database and host UTC.
        var remaining = job.RetryBudget!.DeadlineAt!.Value - job.RetryBudget.LastClaimedAt!.Value - clock.GetElapsedTime(claimingAt);
        return metrics is null ? await ProcessAttemptAsync(job, settings, remaining, ct)
            : await metrics.TrackAsync(MetricOperation.Attempt, () => ProcessAttemptAsync(job, settings, remaining, ct),
                result => result is null ? MetricOutcome.Error : MeteredJobStore.TerminalOutcome(result));
    }

    private async Task<QualityJob?> ProcessAttemptAsync(QualityJob job, JobLeaseOptions settings, TimeSpan remaining, CancellationToken ct)
    {
        await using var lease = new JobLease(store, job, settings, clock, ct, remaining);
        var processing = ProcessClaimedAsync(job, lease, lease.Token);
        try
        {
            try { return await processing.WaitAsync(lease.Token); }
            catch (OperationCanceledException) when (lease.BudgetExpired && lease.Failure is null)
            { return await lease.ExhaustTimeBudgetAsync(); }
        }
        catch (Exception ex)
        {
            if (lease.CancelledJob is { } cancelled) return cancelled;
            // A checkpoint/renewal may discover revoked ownership before the polling loop.
            if (ex is LeaseLostException || lease.Failure is LeaseLostException)
            {
                var latest = await store.GetAsync(job.Id, ct);
                if (latest?.Status == JobStatus.Cancelled) return latest;
            }
            if (lease.Failure is not null) throw lease.Failure;
            throw;
        }
        finally { JobLease.Observe(processing); }
    }

    private async Task<QualityJob> ProcessClaimedAsync(QualityJob job, JobLease lease, CancellationToken ct)
    {
        if (job.Status == JobStatus.Queued)
            job = await lease.SaveAsync(job.TransitionTo(JobStatus.Normalizing, clock.GetUtcNow(), "Normalization started"), ct);
        if (job.Status == JobStatus.Normalizing) job = await NormalizeAsync(job, lease, ct);
        if (job.Status == JobStatus.Planning) job = await PlanAsync(job, lease, ct);
        return job;
    }

    private async Task<QualityJob> NormalizeAsync(QualityJob job, JobLease lease, CancellationToken ct)
    {
        var inputHash = Hash(new { job.Reference, Version = "normalize/v1" });
        var operation = Operation(job, "Normalize");
        if (operation is not null && operation.InputHash != inputHash)
            return await FailRecoveryAsync(job, lease, operation, "provider_operation_input_mismatch", ct);
        if (job.Requirement is null)
        {
            if (operation is not null && operation.Status != ProviderOperationStatus.Started)
                return await FailRecoveryAsync(job, lease, operation, "provider_operation_checkpoint_invalid", ct);
            var budget = job.RetryBudget!;
            Requirement requirement;
            while (true)
            {
                if (operation is not null && operation.Attempts >= budget.MaxNormalizationAttempts)
                    return await lease.SaveAsync(job.ExhaustBudget("normalization_retry_budget_exhausted", clock.GetUtcNow()), ct);
                if (operation is not null)
                {
                    if (operation.RetryAt is null)
                    {
                        operation = operation with { RetryAt = clock.GetUtcNow() + RetryDelay(budget, operation.Attempts) };
                        job = await lease.SaveAsync(WithOperation(job, operation), ct);
                    }
                    var delay = operation.RetryAt.Value - clock.GetUtcNow();
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, ct);
                }
                // Persist the invocation before making a safe read. Crashes consume an attempt too.
                operation = operation is null ? Start("Normalize", job.Reference.Source, inputHash)
                    : operation with { Attempts = operation.Attempts + 1, RetryAt = null, Error = null };
                job = await lease.SaveAsync(WithOperation(job, operation), ct);
                try { requirement = metrics is null ? await source.NormalizeAsync(job.Reference, ct)
                    : await metrics.TrackAsync(MetricOperation.Normalize, () => source.NormalizeAsync(job.Reference, ct)); break; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (LeaseLostException) { throw; }
                catch (Exception ex) when (TransientReadFailure(ex))
                {
                    var delay = RetryDelay(budget, operation.Attempts);
                    if (ex is RequirementRequestException { RetryAfter: { } requested } && requested > delay) delay = requested;
                    if (operation.Attempts >= budget.MaxNormalizationAttempts || delay.TotalMilliseconds > budget.MaxDelayMilliseconds)
                        return await lease.SaveAsync(job.ExhaustBudget("normalization_retry_budget_exhausted", clock.GetUtcNow()), ct);
                    operation = operation with { RetryAt = clock.GetUtcNow() + delay, Error = "requirement_transient_failure" };
                    job = await lease.SaveAsync(WithOperation(job, operation), ct);
                }
                catch (Exception) { return await FailProviderAsync(job, lease, operation, "provider_failure", null, ct); }
            }
            operation = operation with { Status = ProviderOperationStatus.Completed, FinishedAt = clock.GetUtcNow(), RetryAt = null, Error = null,
                Provider = requirement.IsStub ? "stub" : requirement.Reference.Source };
            job = WithOperation(job with { Requirement = requirement, Decisions = [..job.Decisions,
                Decision(job, "Normalize", operation.Provider, "normalize/v1",
                    requirement.IsStub ? "Created synthetic requirement" : "Imported source requirement", requirement.IsStub)] }, operation);
            // Persist result before advancing: recovery can finish the transition without another read.
            job = await lease.SaveAsync(job, ct);
        }
        else if (operation is not null && operation.Status != ProviderOperationStatus.Completed)
            return await FailRecoveryAsync(job, lease, operation, "provider_operation_checkpoint_invalid", ct);
        return await lease.SaveAsync(job.TransitionTo(JobStatus.Planning, clock.GetUtcNow(), "Requirement persisted; planning started"), ct);
    }

    private async Task<QualityJob> PlanAsync(QualityJob job, JobLease lease, CancellationToken ct)
    {
        var inputHash = Hash(new { job.Requirement, PromptVersion });
        var operation = Operation(job, "Plan");
        if (operation is not null && operation.InputHash != inputHash)
            return await FailRecoveryAsync(job, lease, operation, "provider_operation_input_mismatch", ct);
        if (job.TestPlan is null)
        {
            // Started was committed before the call. Without a committed result we cannot tell
            // whether the remote provider accepted it, so a new worker must not call it again.
            if (operation is not null)
                return await FailRecoveryAsync(job, lease, operation, operation.Status == ProviderOperationStatus.Started
                    ? "provider_operation_outcome_unknown" : "provider_operation_checkpoint_invalid", ct);
            operation = Start("Plan", llm.Name, inputHash);
            job = await lease.SaveAsync(WithOperation(job, operation), ct);
            TestPlan plan;
            try { plan = metrics is null ? await llm.PlanAsync(job.Requirement!, PromptVersion, ct)
                : await metrics.TrackAsync(MetricOperation.Plan, () => llm.PlanAsync(job.Requirement!, PromptVersion, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (LeaseLostException) { throw; }
            catch (PlanningException ex) { return await FailProviderAsync(job, lease, operation, ex.Code, ex.Metadata, ct); }
            catch (Exception) { return await FailProviderAsync(job, lease, operation, "provider_failure", null, ct); }
            operation = operation with { Status = ProviderOperationStatus.Completed, FinishedAt = clock.GetUtcNow() };
            job = WithOperation(job with { TestPlan = plan, Decisions = [..job.Decisions,
                Decision(job, "Plan", operation.Provider, PromptVersion,
                    plan.IsStub ? "Created structured stub test plan" : "Created validated plan; review coverage gaps", plan.IsStub, plan.Planning)] }, operation);
            job = await lease.SaveAsync(job, ct);
        }
        else if (operation is not null && operation.Status != ProviderOperationStatus.Completed)
            return await FailRecoveryAsync(job, lease, operation, "provider_operation_checkpoint_invalid", ct);
        return await lease.SaveAsync(job.TransitionTo(JobStatus.Completed, clock.GetUtcNow(), "Planning completed; no tests executed"), ct);
    }

    private async Task<QualityJob> FailProviderAsync(QualityJob job, JobLease lease, ProviderOperation operation, string code,
        PlanningMetadata? metadata, CancellationToken ct)
    {
        // Exception text can contain credentials or source content; persist only known error codes.
        job = WithOperation(job with { Error = code }, operation with {
            Status = ProviderOperationStatus.Failed, FinishedAt = clock.GetUtcNow(), Error = code });
        if (metadata is not null)
            job = job with { Decisions = [..job.Decisions,
                Decision(job, "Plan", operation.Provider, PromptVersion, code, false, metadata)] };
        return await lease.SaveAsync(job.TransitionTo(JobStatus.Failed, clock.GetUtcNow(), "Provider processing failed"), ct);
    }

    private async Task<QualityJob> FailRecoveryAsync(QualityJob job, JobLease lease, ProviderOperation operation, string code, CancellationToken ct)
    {
        job = WithOperation(job with { Error = code, Decisions = [..job.Decisions,
            Decision(job, operation.Stage, operation.Provider, operation.Stage == "Plan" ? PromptVersion : "normalize/v1",
                "Provider operation requires review; automatic replay was stopped", false)] },
            operation with { Status = ProviderOperationStatus.OutcomeUnknown, FinishedAt = clock.GetUtcNow(), Error = code });
        return await lease.SaveAsync(job.TransitionTo(JobStatus.Failed, clock.GetUtcNow(), "Provider operation requires review"), ct);
    }

    private static bool TransientReadFailure(Exception error) => error is TimeoutException or OperationCanceledException ||
        error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode is 408 or 429 or >= 500 and <= 599);
    private static TimeSpan RetryDelay(JobRetryBudget budget, int attempts)
        => TimeSpan.FromMilliseconds(Math.Min(budget.MaxDelayMilliseconds,
            budget.InitialDelayMilliseconds * Math.Pow(2, Math.Min(attempts - 1, 20))));

    private ProviderOperation Start(string stage, string provider, string inputHash)
        => new(Guid.NewGuid().ToString("N"), stage, provider, inputHash, ProviderOperationStatus.Started, 1, clock.GetUtcNow());
    private static ProviderOperation? Operation(QualityJob job, string stage)
        => job.ProviderOperations?.SingleOrDefault(o => o.Stage == stage);
    private static QualityJob WithOperation(QualityJob job, ProviderOperation operation)
        => job with { ProviderOperations = [..(job.ProviderOperations ?? []).Where(o => o.Stage != operation.Stage), operation] };
    private static string Hash<T>(T input)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options)));
    private AgentDecision Decision(QualityJob job, string stage, string provider, string prompt, string summary, bool isStub = true, PlanningMetadata? planning = null)
        => new(Guid.NewGuid().ToString("N"), job.Id, stage, provider, prompt, summary, clock.GetUtcNow(), isStub, Planning: planning);
}
