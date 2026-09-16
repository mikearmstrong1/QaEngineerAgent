using System.Globalization;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class MetricsIntegrationTests
{
    [Fact]
    public Task PipelineReplayAndCancellationReportObservedOperations() => WithStore(VerifyPipeline);

    [PostgresFact]
    public Task PostgresPipelineReportsTheSameOperationSemantics() => MigrationTests.InSchema(async source =>
    {
        var metrics = new JobMetrics();
        var store = new MeteredJobStore(new PostgresJobStore(source), metrics);
        await store.InitializeAsync(default);
        await VerifyPipeline(store, metrics);
    });

    private static async Task VerifyPipeline(IJobStore store, JobMetrics metrics)
    {
        var service = new JobService(store, new StubRequirementSource(), new StubLlmProvider(), TimeProvider.System, metrics: metrics);
        var request = new JobRequest(new("stub", "private-reference"));
        var job = await service.SubmitAsync(request, default, "private-key");
        await service.SubmitAsync(request, default, "private-key");
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => service.SubmitAsync(new(new("stub", "different")), default, "private-key"));
        Assert.Equal(JobStatus.Completed, (await service.ProcessNextAsync(job.Id, default))!.Status);
        Assert.Null(await service.ProcessNextAsync(job.Id, default));
        Assert.Equal(JobStatus.Completed, (await service.CancelAsync(job.Id, default))!.Status);
        var cancelled = await service.SubmitAsync(request, default);
        await service.CancelAsync(cancelled.Id, default);
        await service.CancelAsync(cancelled.Id, default);
        await service.CancelAsync(Guid.NewGuid().ToString("N"), default);
        var output = metrics.Render();
        Has(output, "submit", "success", 2);
        Has(output, "submit", "replayed", 1);
        Has(output, "submit", "conflict", 1);
        Has(output, "claim", "idle", 1);
        Has(output, "attempt", "completed", 1);
        Has(output, "plan", "success", 1);
        Has(output, "normalize", "success", 1);
        Has(output, "save", "completed", 1);
        Has(output, "cancel", "cancelled", 2); // Requests, not distinct cancellations.
        Has(output, "cancel", "conflict", 1);
        Has(output, "cancel", "missing", 1);
        Assert.DoesNotContain(job.Id, output);
        Assert.DoesNotContain("private", output);
        Assert.Equal(output, metrics.Render()); // Scraping does not mutate the metrics.
    }

    [Fact]
    public Task RetryAndBudgetFailuresAreVisibleWithoutChangingDurableState() => WithStore(async (store, metrics) =>
    {
        var service = new JobService(store, new BrokenSource(), new StubLlmProvider(), TimeProvider.System,
            retryOptions: new RetryBudgetOptions(MaxNormalizationAttempts: 2, InitialDelayMilliseconds: 1, MaxDelayMilliseconds: 1), metrics: metrics);
        var job = await service.SubmitAsync(new(new("stub", "retry")), default);
        var failed = (await service.ProcessNextAsync(job.Id, default))!;
        Assert.Equal("normalization_retry_budget_exhausted", failed.Error);
        Has(metrics.Render(), "normalize", "error", 2);
        Has(metrics.Render(), "attempt", "budget_exhausted", 1);
        Has(metrics.Render(), "save", "budget_exhausted", 1);
        var expired = QualityJob.Create(new(new("stub", "expired")), DateTimeOffset.UtcNow)
            with { RetryBudget = new JobRetryBudget(WorkerAttempts: 5) };
        await store.CreateAsync(expired, default);
        Assert.Equal(JobStatus.Failed, (await service.ProcessNextAsync(expired.Id, default))!.Status);
        Has(metrics.Render(), "claim", "budget_exhausted", 1);
        Has(metrics.Render(), "attempt", "budget_exhausted", 1); // Exhausted claim never starts an attempt.
    });

    [Fact]
    public Task LeaseLossAndInterruptionKeepTheirExceptionTypes() => WithStore(async (store, metrics) =>
    {
        var job = QualityJob.Create(new(new("stub", "fence")), DateTimeOffset.UtcNow);
        await store.CreateAsync(job, default);
        var owner = (await store.ClaimAsync(job.Id, TimeSpan.FromSeconds(5), default))!;
        await store.CancelAsync(job.Id, default);
        await Assert.ThrowsAsync<LeaseLostException>(() => store.SaveAsync(owner, default));
        await Assert.ThrowsAsync<LeaseLostException>(() => store.RenewLeaseAsync(owner, TimeSpan.FromSeconds(5), default));
        Has(metrics.Render(), "save", "lease_lost", 1);
        Has(metrics.Render(), "renew", "lease_lost", 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => metrics.TrackAsync(MetricOperation.Plan,
            () => Task.FromCanceled<int>(cancelled.Token)));
        Has(metrics.Render(), "plan", "interrupted", 1);
        Assert.Contains("quality_operations_active{operation=\"plan\"} 0\n", metrics.Render());
    });

    [Fact]
    public Task DetachedProviderRemainsActiveButCannotOverwriteCancellation() => WithStore(async (store, metrics) =>
    {
        var provider = new DelayedPlanner();
        var options = new JobLeaseOptions { CancellationPollInterval = TimeSpan.FromMilliseconds(10) };
        var service = new JobService(store, new StubRequirementSource(), provider, TimeProvider.System, options, metrics: metrics);
        var job = await service.SubmitAsync(new(new("stub", "detached")), default);
        var processing = service.ProcessNextAsync(job.Id, default);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.CancelAsync(job.Id, default);
            Assert.Equal(JobStatus.Cancelled, (await processing.WaitAsync(TimeSpan.FromSeconds(5)))!.Status);
            Assert.Contains("quality_operations_active{operation=\"attempt\"} 0\n", metrics.Render());
            Assert.Contains("quality_operations_active{operation=\"plan\"} 1\n", metrics.Render());
            Has(metrics.Render(), "attempt", "cancelled", 1);
            provider.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!metrics.Render().Contains("quality_operations_active{operation=\"plan\"} 0\n"))
                await Task.Delay(10, timeout.Token);
            Has(metrics.Render(), "plan", "success", 1);
            var saved = (await store.GetAsync(job.Id, default))!;
            Assert.Equal(JobStatus.Cancelled, saved.Status);
            Assert.Null(saved.TestPlan);
        }
        finally { provider.Release.TrySetResult(); }
    });

    [Fact]
    public async Task HistogramIsCumulativeConsistentAndCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var metrics = new JobMetrics();
            await metrics.TrackAsync(MetricOperation.Plan, () => Task.FromResult(true));
            var text = metrics.Render();
            Assert.Contains("le=\"0.005\"", text);
            Assert.DoesNotContain("le=\"0,005\"", text);
            var buckets = text.Split('\n').Where(line => line.StartsWith("quality_operation_duration_seconds_bucket{operation=\"plan\""))
                .Select(line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(buckets.Order().ToArray(), buckets);
            Assert.Equal(1, buckets[^1]);
            Assert.Contains("quality_operation_duration_seconds_count{operation=\"plan\"} 1\n", text);
            Assert.EndsWith("\n", text);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Has(string output, string operation, string outcome, int count)
        => Assert.Contains($"quality_operations_total{{operation=\"{operation}\",outcome=\"{outcome}\"}} {count}\n", output);
    private static async Task WithStore(Func<IJobStore, JobMetrics, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "metrics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metrics = new JobMetrics();
            await action(new MeteredJobStore(new FileJobStore(directory, TimeProvider.System), metrics), metrics);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class DelayedPlanner : ILlmProvider
    {
        public string Name => "private-provider-name";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await new StubLlmProvider().PlanAsync(requirement, promptVersion, default);
        }
    }
    private sealed class BrokenSource : IRequirementSource
    {
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
            => throw new HttpRequestException("private provider details");
    }
}
