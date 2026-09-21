using Npgsql;
using Quality.Domain;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class MigrationTests
{
    [PostgresFact]
    public async Task ConcurrentStartupAdoptsLegacyJobsAndRejectsChangedHistory()
    {
        await InSchema(async source =>
        {
            await Execute(source, """
                CREATE TABLE quality_jobs (
                    id text PRIMARY KEY, created_at timestamptz NOT NULL, status text NOT NULL,
                    revision bigint NOT NULL, lease_token text NULL, lease_until timestamptz NULL, document jsonb NOT NULL
                )
                """);
            var store = new PostgresJobStore(source);
            var job = QualityJob.Create(new(new("stub", "migration")), DateTimeOffset.UtcNow);
            await store.CreateAsync(job, default);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.InitializeAsync(default)));
            Assert.Equal(job.Id, (await store.GetAsync(job.Id, default))!.Id);
            await using var count = source.CreateCommand("SELECT count(*) FROM quality_schema_migrations");
            Assert.Equal(4L, await count.ExecuteScalarAsync());
            await Execute(source, "UPDATE quality_schema_migrations SET sha256='modified'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
            Assert.NotNull(await store.GetAsync(job.Id, default));
        });
    }

    [PostgresFact]
    public async Task NewerAndNoncontiguousHistoryAreRejected()
    {
        await InSchema(async source =>
        {
            var store = new PostgresJobStore(source);
            await store.InitializeAsync(default);
            await Execute(source, "INSERT INTO quality_schema_migrations(version, sha256) VALUES (5, 'future')");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
            await Execute(source, "DELETE FROM quality_schema_migrations WHERE version=1");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
        });
    }

    [PostgresFact]
    public async Task FailedMigrationRollsBackItsLedgerAndSchemaChanges()
    {
        await InSchema(async source =>
        {
            // A malformed legacy table makes the pending index creation fail.
            await Execute(source, "CREATE TABLE quality_jobs (id text PRIMARY KEY)");
            var store = new PostgresJobStore(source);
            await Assert.ThrowsAsync<PostgresException>(() => store.InitializeAsync(default));
            await using var exists = source.CreateCommand("SELECT to_regclass('quality_schema_migrations')::text");
            Assert.Equal(DBNull.Value, await exists.ExecuteScalarAsync());
        });
    }

    internal static async Task InSchema(Func<NpgsqlDataSource, Task> action)
    {
        var connectionString = Environment.GetEnvironmentVariable("QUALITY_TEST_POSTGRES")!;
        await using var admin = NpgsqlDataSource.Create(connectionString);
        var schema = "migration_test_" + Guid.NewGuid().ToString("N");
        await Execute(admin, $"CREATE SCHEMA {schema}");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            await using var isolated = NpgsqlDataSource.Create(settings.ConnectionString);
            await action(isolated);
        }
        finally { await Execute(admin, $"DROP SCHEMA {schema} CASCADE"); }
    }

    private static async Task Execute(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
