using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task FileSubmissionsAreAtomicAndSurviveNewStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idempotency-" + Guid.NewGuid().ToString("N"));
        try { await Verify(() => new FileJobStore(directory, TimeProvider.System)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [PostgresFact]
    public async Task PostgresSubmissionsAreAtomicAndDurable()
    {
        await MigrationTests.InSchema(async source =>
        {
            await new PostgresJobStore(source).InitializeAsync(default);
            await Verify(() => new PostgresJobStore(source));
        });
    }

    private static async Task Verify(Func<IJobStore> store)
    {
        var provider = new CountingProvider();
        JobService Service() => new(store(), new StubRequirementSource(), provider, TimeProvider.System);
        var request = new JobRequest(new("stub", "idempotency"));
        var key = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Service().SubmitAsync(request, default, key)));
        var id = Assert.Single(results.Select(j => j.Id).Distinct());
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Service().SubmitAsync(new(new("stub", "different")), default, key));
        Assert.Equal(0, provider.Calls);
        var owner = await store().ClaimAsync(id, TimeSpan.FromMinutes(5), default);
        Assert.Equal(id, (await Service().SubmitAsync(request, default, key)).Id);
        Assert.Null(await store().ClaimAsync(id, TimeSpan.FromMinutes(5), default));
        await store().SaveAsync(owner! with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
        await Service().ProcessNextAsync(id, default);
        var replay = await Service().SubmitAsync(request, default, key);
        Assert.Equal(id, replay.Id);
        Assert.Equal(JobStatus.Completed, replay.Status);
        Assert.Null(await Service().ProcessNextAsync(replay.Id, default));
        Assert.Equal(1, provider.Calls);
        Assert.NotEqual(id, (await Service().SubmitAsync(request, default, key + "-new")).Id);
        Assert.NotEqual((await Service().SubmitAsync(request, default)).Id, (await Service().SubmitAsync(request, default)).Id);
        var failed = await Service().SubmitAsync(request, default, key + "-failed");
        var failedOwner = await store().ClaimAsync(failed.Id, TimeSpan.FromMinutes(5), default);
        await store().SaveAsync(failedOwner!.TransitionTo(JobStatus.Failed, DateTimeOffset.UtcNow, "test failure"), default);
        Assert.Equal(JobStatus.Failed, (await Service().SubmitAsync(request, default, key + "-failed")).Status);
        Assert.Null(await Service().ProcessNextAsync(failed.Id, default));
        foreach (var invalid in new[] { "", " ", "with space", "a\nb", new string('x', 201), "é" })
            await Assert.ThrowsAsync<ArgumentException>(() => Service().SubmitAsync(request, default, invalid));
    }

    private sealed class CountingProvider : ILlmProvider
    {
        public string Name => "counting";
        public int Calls;
        public Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return new StubLlmProvider().PlanAsync(requirement, promptVersion, ct);
        }
    }
}
