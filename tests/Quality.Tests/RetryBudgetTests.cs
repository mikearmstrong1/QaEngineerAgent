using System.Net;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class RetryBudgetTests
{
    private static readonly RetryBudgetOptions Fast = new(5, 3, 30, 10, 100);
    private static JobService Service(IJobStore store, IRequirementSource? source = null, ILlmProvider? planner = null,
        RetryBudgetOptions? options = null) => new(store, source ?? new StubRequirementSource(), planner ?? new StubLlmProvider(),
            TimeProvider.System, retryOptions: options ?? Fast);

    [Fact]
    public Task FileClaimsConsumeDurableBudgetAtomically() => WithFile(VerifyClaims);
    [Fact]
    public Task FileInterruptedImportsCannotResetAttempts() => WithFile(VerifyInterruptedReads);
    [Fact]
    public Task FileExpiredBudgetStopsBeforeProviders() => WithFile(VerifyExpired);
    [Fact]
    public Task FilePlanningDeadlineRejectsLateResult() => WithFile(VerifyDeadline);

    [PostgresFact]
    public async Task PostgreSqlBudgetsSurviveWorkersAndEnforceDeadline()
    {
        await MigrationTests.InSchema(async source =>
        {
            await new PostgresJobStore(source).InitializeAsync(default);
            Func<IJobStore> store = () => new PostgresJobStore(source);
            await VerifyClaims(store);
            await VerifyInterruptedReads(store);
            await VerifyExpired(store);
            await VerifyDeadline(store);
        });
    }

    private static async Task VerifyClaims(Func<IJobStore> store)
    {
        var job = await Service(store(), options: Fast with { MaxWorkerAttempts = 2 }).SubmitAsync(new(new("stub", "budget")), default);
        DateTimeOffset? deadline = null;
        for (var count = 1; count <= 2; count++)
        {
            var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store().ClaimAsync(job.Id, TimeSpan.FromMinutes(5), default)));
            var owner = Assert.Single(claims, claim => claim is not null)!;
            Assert.Equal(count, owner.RetryBudget!.WorkerAttempts);
            deadline ??= owner.RetryBudget.DeadlineAt;
            Assert.Equal(deadline, owner.RetryBudget.DeadlineAt);
            var renewed = await store().RenewLeaseAsync(owner, TimeSpan.FromMinutes(5), default);
            Assert.Equal(count, renewed.RetryBudget!.WorkerAttempts);
            await Expire(store(), renewed);
        }
        var competing = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store().ClaimAsync(job.Id, TimeSpan.FromMinutes(5), default)));
        var failed = Assert.Single(competing, claim => claim is not null)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal("job_retry_budget_exhausted", failed.Error);
        Assert.Equal(2, failed.RetryBudget!.WorkerAttempts);
        Assert.Null(failed.LeaseToken);
        Assert.Null(await store().ClaimAsync(job.Id, TimeSpan.FromMinutes(5), default));
        // Legacy records get fixed default limits once, atomically with their first claim.
        var legacy = QualityJob.Create(new(new("stub", "legacy")), DateTimeOffset.UtcNow);
        await store().CreateAsync(legacy, default);
        var adopted = await store().ClaimAsync(legacy.Id, TimeSpan.FromMinutes(5), default);
        Assert.Equal(5, adopted!.RetryBudget!.MaxWorkerAttempts);
        Assert.Equal(1, adopted.RetryBudget.WorkerAttempts);
    }

    private static async Task VerifyInterruptedReads(Func<IJobStore> store)
    {
        var job = await Service(store(), options: Fast with { MaxNormalizationAttempts = 2 }).SubmitAsync(new(new("stub", "interrupted")), default, "key-" + Guid.NewGuid());
        string? operationId = null;
        for (var count = 1; count <= 2; count++)
        {
            using var cancel = new CancellationTokenSource();
            var source = new Source((_, _) => { cancel.Cancel(); return Task.FromCanceled<Requirement>(cancel.Token); });
            // Replacement workers with higher configured limits cannot reset the saved policy.
            var service = Service(store(), source, options: Fast with { MaxNormalizationAttempts = 10 });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessNextAsync(job.Id, cancel.Token));
            var interrupted = (await store().GetAsync(job.Id, default))!;
            var operation = Assert.Single(interrupted.ProviderOperations!);
            operationId ??= operation.Id;
            Assert.Equal(operationId, operation.Id);
            Assert.Equal(count, operation.Attempts);
            Assert.Equal(2, interrupted.RetryBudget!.MaxNormalizationAttempts);
            await Expire(store(), interrupted);
        }
        var forbidden = new Source((_, _) => throw new InvalidOperationException("Must not be called"));
        var failed = await Service(store(), forbidden).ProcessNextAsync(job.Id, default);
        Assert.Equal("normalization_retry_budget_exhausted", failed!.Error);
        Assert.Equal(0, forbidden.Calls);
        Assert.Equal(2, Assert.Single(failed.ProviderOperations!).Attempts);
    }

    private static async Task VerifyExpired(Func<IJobStore> store)
    {
        var job = await Service(store()).SubmitAsync(new(new("stub", "expired")), default);
        var owner = (await store().ClaimAsync(job.Id, TimeSpan.FromMinutes(5), default))!;
        owner = await store().SaveAsync(owner with { RetryBudget = owner.RetryBudget! with { DeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1) } }, default);
        await Expire(store(), owner);
        var forbidden = new Source((_, _) => throw new InvalidOperationException());
        var failed = await Service(store(), forbidden).ProcessNextAsync(job.Id, default);
        Assert.Equal("job_time_budget_exhausted", failed!.Error);
        Assert.Equal(0, forbidden.Calls);
        Assert.Equal(1, failed.RetryBudget!.WorkerAttempts);
    }

    private static async Task VerifyDeadline(Func<IJobStore> store)
    {
        var planner = new LatePlanner();
        var service = Service(store(), planner: planner, options: Fast with { MaxDurationSeconds = 1 });
        var job = await service.SubmitAsync(new(new("stub", "deadline")), default);
        var processing = service.ProcessNextAsync(job.Id, default);
        try
        {
            await planner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failed = (await processing.WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.Equal("job_time_budget_exhausted", failed.Error);
            var operation = failed.ProviderOperations!.Single(o => o.Stage == "Plan");
            Assert.Equal(ProviderOperationStatus.OutcomeUnknown, operation.Status);
            Assert.Equal("provider_operation_outcome_unknown", operation.Error);
            planner.Release.TrySetResult();
            await planner.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            var persisted = (await store().GetAsync(job.Id, default))!;
            Assert.Equal(failed.Revision, persisted.Revision);
            Assert.Null(persisted.TestPlan);
            Assert.Null(await service.ProcessNextAsync(job.Id, default));
        }
        finally { planner.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(503, 2, "Completed", null, 3)]
    [InlineData(429, 5, "Failed", "normalization_retry_budget_exhausted", 3)]
    [InlineData(408, 1, "Completed", null, 2)]
    [InlineData(401, 5, "Failed", "provider_failure", 1)]
    [InlineData(404, 5, "Failed", "provider_failure", 1)]
    public Task TransientReadsRetryWithinBudgetAndPermanentErrorsStop(int status, int failures, string expectedStatus, string? error, int calls)
        => WithFile(async store =>
        {
            var source = new Source((n, reference) => n <= failures
                ? throw new HttpRequestException("private details", null, (HttpStatusCode)status)
                : new StubRequirementSource().NormalizeAsync(reference, default));
            var service = Service(store(), source);
            var job = await service.SubmitAsync(new(new("stub", "transient")), default);
            var result = (await service.ProcessNextAsync(job.Id, default))!;
            Assert.Equal(expectedStatus, result.Status.ToString());
            Assert.Equal(error, result.Error);
            Assert.Equal(calls, source.Calls);
            Assert.Equal(calls, result.ProviderOperations!.Single(o => o.Stage == "Normalize").Attempts);
            Assert.DoesNotContain("private details", System.Text.Json.JsonSerializer.Serialize(result));
        });

    [Fact]
    public Task DeadlineIncludesBackoffWithoutAnotherRead() => WithFile(async store =>
    {
        var source = new Source((_, _) => throw new RequirementRequestException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)));
        var service = Service(store(), source, options: Fast with { MaxDurationSeconds = 1, MaxDelayMilliseconds = 3000 });
        var job = await service.SubmitAsync(new(new("stub", "backoff-deadline")), default);
        var result = await service.ProcessNextAsync(job.Id, default);
        Assert.Equal("job_time_budget_exhausted", result!.Error);
        Assert.Equal(1, source.Calls);
    });

    [Fact]
    public Task LongRetryAfterStopsInsteadOfRetryingEarly() => WithFile(async store =>
    {
        var source = new Source((_, _) => throw new RequirementRequestException(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1)));
        var service = Service(store(), source);
        var job = await service.SubmitAsync(new(new("stub", "retry-after")), default);
        Assert.Equal("normalization_retry_budget_exhausted", (await service.ProcessNextAsync(job.Id, default))!.Error);
        Assert.Equal(1, source.Calls);
    });

    [Fact]
    public Task RestartPreservesBackoffAndDoesNotSpendAnAttemptBeforeCalling() => WithFile(async store =>
    {
        using var cancel = new CancellationTokenSource();
        var source = new Source((_, _) => { cancel.Cancel(); return Task.FromCanceled<Requirement>(cancel.Token); });
        var job = await Service(store(), source).SubmitAsync(new(new("stub", "restart-backoff")), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(store(), source).ProcessNextAsync(job.Id, cancel.Token));
        var interrupted = (await store().GetAsync(job.Id, default))!;
        var operation = Assert.Single(interrupted.ProviderOperations!);
        var due = DateTimeOffset.UtcNow.AddMilliseconds(600);
        interrupted = await store().SaveAsync(interrupted with { ProviderOperations = [operation with { RetryAt = due }] }, default);
        await Expire(store(), interrupted);
        var replacement = new Source((_, reference) =>
        {
            Assert.True(DateTimeOffset.UtcNow >= due);
            return new StubRequirementSource().NormalizeAsync(reference, default);
        });
        var completed = await Service(store(), replacement).ProcessNextAsync(job.Id, default);
        Assert.Equal(JobStatus.Completed, completed!.Status);
        Assert.Equal(1, replacement.Calls);
        Assert.Equal(2, completed.ProviderOperations!.Single(o => o.Stage == "Normalize").Attempts);
    });

    [Theory]
    [InlineData(0, 3, 900, 10, 100)]
    [InlineData(5, 11, 900, 10, 100)]
    [InlineData(5, 3, 0, 10, 100)]
    [InlineData(5, 3, 900, 0, 100)]
    [InlineData(5, 3, 900, 100, 10)]
    public void InvalidBudgetsAreRejected(int workers, int reads, int seconds, int initial, int maximum)
        => Assert.Throws<ArgumentException>(() => new RetryBudgetOptions(workers, reads, seconds, initial, maximum).Snapshot());

    private static Task<QualityJob> Expire(IJobStore store, QualityJob job)
        => store.SaveAsync(job with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
    private static async Task WithFile(Func<Func<IJobStore>, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-tests-" + Guid.NewGuid().ToString("N"));
        try { await action(() => new FileJobStore(directory, TimeProvider.System)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Source(Func<int, RequirementReference, Task<Requirement>> read) : IRequirementSource
    {
        public int Calls;
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct) => read(++Calls, reference);
    }
    private sealed class LatePlanner : ILlmProvider
    {
        public string Name => "late";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TestPlan> PlanAsync(Requirement requirement, string version, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await new StubLlmProvider().PlanAsync(requirement, version, default);
            Exited.TrySetResult();
            return result;
        }
    }
}
