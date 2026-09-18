using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class JobTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "quality-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private FileJobStore Store => new(directory, clock);
    private JobService Service(ILlmProvider? llm = null) => new(Store, new StubRequirementSource(), llm ?? new StubLlmProvider(), clock);
    private static JobRequest Request => new(new("jira", "AUTH-1427"));

    [Fact]
    public async Task PipelinePersistsRequirementPlanAndOrderedHistoryAcrossStoreInstances()
    {
        var submitted = await Service().SubmitAsync(Request, default);
        Assert.Equal(JobStatus.Queued, (await Store.GetAsync(submitted.Id, default))!.Status);
        var completed = await Service().ProcessNextAsync(submitted.Id, default);
        var loaded = await Store.GetAsync(submitted.Id, default);
        Assert.Equal(JobStatus.Completed, loaded!.Status);
        Assert.Equal(completed!.Revision, loaded.Revision);
        Assert.Equal([JobStatus.Queued, JobStatus.Normalizing, JobStatus.Planning, JobStatus.Completed], loaded.Transitions.Select(t => t.Status));
        Assert.True(loaded.Requirement!.IsStub);
        Assert.True(loaded.TestPlan!.IsStub);
        Assert.Equal(loaded.Requirement.Id, loaded.TestPlan.RequirementId);
        Assert.Equal("AC-1", Assert.Single(Assert.Single(loaded.TestPlan.TestCases).AcceptanceCriterionIds));
        Assert.Equal(2, loaded.Decisions.Length);
        Assert.Null(loaded.LeaseToken);
        Assert.Null(await Service().ProcessNextAsync(submitted.Id, default));
    }
    [Theory]
    [InlineData("jira", "bad")]
    [InlineData("jira", "AUTH-0")]
    [InlineData("other", "AUTH-1")]
    [InlineData("stub", " ")]
    public async Task InvalidReferencesNeverCreateJobs(string source, string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().SubmitAsync(new(new(source, id)), default));
        Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public void StateMachineRejectsSkippingStages()
    {
        var job = QualityJob.Create(Request, clock.GetUtcNow());
        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(JobStatus.Completed, clock.GetUtcNow(), "skip"));
        var normalizing = job.TransitionTo(JobStatus.Normalizing, clock.GetUtcNow(), "start");
        Assert.Throws<InvalidOperationException>(() => normalizing.TransitionTo(JobStatus.Planning, clock.GetUtcNow(), "missing requirement"));
    }
    [Fact]
    public async Task ProviderFailurePersistsSanitizedFailure()
    {
        var job = await Service().SubmitAsync(Request, default);
        var failed = await Service(new BrokenLlm()).ProcessNextAsync(job.Id, default);
        Assert.Equal(JobStatus.Failed, failed!.Status);
        Assert.Equal("provider_failure", failed.Error);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(failed));
        Assert.NotNull((await Store.GetAsync(job.Id, default))!.Requirement);
    }
    [Fact]
    public async Task ConcurrentClaimsHaveExactlyOneOwner()
    {
        var job = await Service().SubmitAsync(Request, default);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Store.ClaimAsync(job.Id, TimeSpan.FromMinutes(5), default)));
        Assert.Single(claims, j => j is not null);
    }
    [Fact]
    public async Task ExpiredClaimCanBeRecoveredAndOldOwnerCannotSave()
    {
        var job = await Service().SubmitAsync(Request, default);
        var old = (await Store.ClaimAsync(job.Id, TimeSpan.FromSeconds(1), default))!;
        clock.Advance(TimeSpan.FromSeconds(2));
        var recovered = (await Store.ClaimAsync(job.Id, TimeSpan.FromMinutes(5), default))!;
        Assert.NotEqual(old.LeaseToken, recovered.LeaseToken);
        await Assert.ThrowsAsync<LeaseLostException>(() => Store.SaveAsync(old, default));
        await Store.SaveAsync(recovered.TransitionTo(JobStatus.Normalizing, clock.GetUtcNow(), "recovered"), default);
    }
    [Fact]
    public async Task ResumePlanningDoesNotRepeatNormalization()
    {
        var job = await Service().SubmitAsync(Request, default);
        job = (await Store.ClaimAsync(job.Id, TimeSpan.FromSeconds(1), default))!;
        job = await Store.SaveAsync(job.TransitionTo(JobStatus.Normalizing, clock.GetUtcNow(), "start"), default);
        job = job with { Requirement = await new StubRequirementSource().NormalizeAsync(job.Reference, default) };
        await Store.SaveAsync(job.TransitionTo(JobStatus.Planning, clock.GetUtcNow(), "checkpoint"), default);
        clock.Advance(TimeSpan.FromSeconds(2));
        var service = new JobService(Store, new ForbiddenSource(), new StubLlmProvider(), clock);
        Assert.Equal(JobStatus.Completed, (await service.ProcessNextAsync(job.Id, default))!.Status);
    }
    [Fact]
    public async Task CancellationLeavesRecoverableJob()
    {
        var job = await Service().SubmitAsync(Request, default);
        using var cancel = new CancellationTokenSource();
        var service = new JobService(Store, new CancelSource(cancel), new StubLlmProvider(), clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessNextAsync(job.Id, cancel.Token));
        Assert.Equal(JobStatus.Normalizing, (await Store.GetAsync(job.Id, default))!.Status);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(JobStatus.Completed, (await Service().ProcessNextAsync(job.Id, default))!.Status);
    }
    [Fact]
    public async Task UnconfiguredExecutorDoesNotReportPassingTests()
    {
        var job = await Service().SubmitAsync(Request, default);
        var done = (await Service().ProcessNextAsync(job.Id, default))!;
        await Assert.ThrowsAsync<NotSupportedException>(() => new StubTestExecutor().ExecuteAsync(done.TestPlan!, new Uri("http://localhost"), default));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset startedAt = TimeProvider.System.GetUtcNow();
        private readonly long startTimestamp = TimeProvider.System.GetTimestamp();
        private long offsetTicks;

        public override DateTimeOffset GetUtcNow() => startedAt + TimeProvider.System.GetElapsedTime(startTimestamp)
            + TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

        public void Advance(TimeSpan duration) => Interlocked.Add(ref offsetTicks, duration.Ticks);
    }
    private sealed class BrokenLlm : ILlmProvider
    {
        public string Name => "broken";
        public Task<TestPlan> PlanAsync(Requirement requirement, string version, CancellationToken ct) => throw new InvalidOperationException("secret");
    }
    private sealed class ForbiddenSource : IRequirementSource
    {
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct) => throw new InvalidOperationException("Should resume planning");
    }
    private sealed class CancelSource(CancellationTokenSource cancel) : IRequirementSource
    {
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
        { cancel.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
    }
}
