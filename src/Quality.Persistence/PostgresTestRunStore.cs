using System.Text.Json;
using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

// PostgreSQL owns queryable run metadata; the execution volume keeps large artifacts and runner inputs.
public sealed class PostgresTestRunStore(NpgsqlDataSource dataSource, string artifactRoot) : ITestRunStore
{
    private readonly FileTestRunStore files = new(artifactRoot);

    public string DirectoryFor(string id) => files.DirectoryFor(id);

    public async Task SaveAsync(TestRun run, CancellationToken ct)
    {
        await files.SaveAsync(run, ct);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO quality_test_runs(id, test_plan_id, started_at, status, document)
            VALUES ($1, $2, $3, $4, $5::jsonb)
            ON CONFLICT (id) DO UPDATE SET status=EXCLUDED.status, document=EXCLUDED.document
            """);
        command.Parameters.AddWithValue(run.Id);
        command.Parameters.AddWithValue(run.TestPlanId);
        command.Parameters.AddWithValue(run.StartedAt);
        command.Parameters.AddWithValue(run.Status);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(run, ContractJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TestRun?> GetAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid run ID");
        await using var command = dataSource.CreateCommand("SELECT document::text FROM quality_test_runs WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<TestRun>(json, ContractJson.Options) : null;
    }

    public async Task<IReadOnlyList<TestRun>> ListByPlanAsync(string testPlanId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testPlanId) || testPlanId.Length > 200 || testPlanId.Any(char.IsControl))
            throw new ArgumentException("Invalid test plan ID");
        await using var command = dataSource.CreateCommand("""
            SELECT document::text FROM quality_test_runs WHERE test_plan_id=$1
            ORDER BY started_at DESC, id DESC LIMIT 100
            """);
        command.Parameters.AddWithValue(testPlanId);
        var runs = new List<TestRun>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) runs.Add(JsonSerializer.Deserialize<TestRun>(reader.GetString(0), ContractJson.Options)!);
        return runs;
    }
}
