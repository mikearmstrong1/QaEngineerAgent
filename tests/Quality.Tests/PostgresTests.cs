using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUALITY_TEST_POSTGRES")))
            Skip = "Set QUALITY_TEST_POSTGRES to a dedicated PostgreSQL test database";
    }
}
public sealed class PostgresTests
{
    [PostgresFact]
    public async Task DurablePipelineAndConcurrentClaims()
    {
        await using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("QUALITY_TEST_POSTGRES")!);
        var store = new PostgresJobStore(dataSource);
        await Task.WhenAll(store.InitializeAsync(default), new PostgresJobStore(dataSource).InitializeAsync(default));
        var service = new JobService(store, new StubRequirementSource(), new StubLlmProvider(), TimeProvider.System);
        var job = await service.SubmitAsync(new(new("jira", "AUTH-1427")), default);
        try
        {
            var completed = await service.ProcessNextAsync(job.Id, default);
            Assert.Equal(JobStatus.Completed, completed!.Status);
            Assert.Equal(4, (await new PostgresJobStore(dataSource).GetAsync(job.Id, default))!.Transitions.Length);
            Assert.Null(await store.ClaimAsync(job.Id, TimeSpan.FromSeconds(1), default));
        }
        finally { await Delete(dataSource, job.Id); }
        var queued = await service.SubmitAsync(new(new("stub", "concurrency")), default);
        try
        {
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(queued.Id, TimeSpan.FromMinutes(5), default)));
            var owner = Assert.Single(claims, j => j is not null)!;
            var saved = await store.SaveAsync(owner.TransitionTo(JobStatus.Normalizing, DateTimeOffset.UtcNow, "test"), default);
            await Assert.ThrowsAsync<LeaseLostException>(() => store.SaveAsync(owner, default));
            Assert.Equal(owner.Revision + 1, saved.Revision);
        }
        finally { await Delete(dataSource, queued.Id); }
    }
    private static async Task Delete(NpgsqlDataSource source, string id)
    {
        await using var command = source.CreateCommand("DELETE FROM quality_jobs WHERE id=$1");
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync();
    }
}
