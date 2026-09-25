using System.Text.Json;
using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

public sealed class PostgresExecutionPolicyStore(NpgsqlDataSource dataSource) : IExecutionPolicyStore
{
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<IReadOnlyList<ExecutionPolicyRevision>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT document::text FROM quality_execution_policy_revisions
            ORDER BY name, created_at DESC, version
            """);
        var items = new List<ExecutionPolicyRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Deserialize(reader.GetString(0)));
        return items;
    }

    public async Task<ExecutionPolicyRevision?> GetAsync(string name, string version, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT document::text FROM quality_execution_policy_revisions WHERE name=$1 AND version=$2
            """);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(version);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }

    public async Task<ExecutionPolicyRevision> CreateAsync(ExecutionPolicy policy, bool activate, DateTimeOffset now, CancellationToken ct)
    {
        ExecutionPolicyCatalog.ValidatePolicy(policy);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockNameAsync(connection, policy.Name, ct);
        var existing = await GetForUpdateAsync(connection, policy.Name, policy.Version, ct);
        if (existing is not null)
        {
            if (existing.Fingerprint != policy.Fingerprint()) throw new ExecutionPolicyConflictException();
            if (activate && existing.Status != ExecutionPolicyStatus.Active)
                existing = await SetStatusAsync(connection, existing, ExecutionPolicyStatus.Active, now, ct);
            await transaction.CommitAsync(ct);
            return existing;
        }
        if (activate) await RetireActiveAsync(connection, policy.Name, now, ct);
        var created = new ExecutionPolicyRevision(policy,
            activate ? ExecutionPolicyStatus.Active : ExecutionPolicyStatus.Draft, now, now);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO quality_execution_policy_revisions(name, version, fingerprint, status, created_at, updated_at, document)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)
            """;
        Bind(insert, created);
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return created;
    }

    public async Task<ExecutionPolicyRevision> SetStatusAsync(string name, string version, ExecutionPolicyStatus status,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockNameAsync(connection, name, ct);
        var current = await GetForUpdateAsync(connection, name, version, ct)
            ?? throw new ArgumentException("Execution policy revision was not found");
        var updated = await SetStatusAsync(connection, current, status, now, ct);
        await transaction.CommitAsync(ct);
        return updated;
    }

    private static async Task<ExecutionPolicyRevision> SetStatusAsync(NpgsqlConnection connection,
        ExecutionPolicyRevision current, ExecutionPolicyStatus status, DateTimeOffset now, CancellationToken ct)
    {
        FileExecutionPolicyStore.ValidateTransition(current.Status, status);
        if (current.Status == status) return current;
        if (status == ExecutionPolicyStatus.Active) await RetireActiveAsync(connection, current.Name, now, ct);
        var updated = current with { Status = status, UpdatedAt = now };
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quality_execution_policy_revisions SET status=$3, updated_at=$4, document=$5::jsonb
            WHERE name=$1 AND version=$2
            """;
        command.Parameters.AddWithValue(updated.Name);
        command.Parameters.AddWithValue(updated.Version);
        command.Parameters.AddWithValue(updated.Status.ToString());
        command.Parameters.AddWithValue(updated.UpdatedAt);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(updated, ContractJson.Options));
        await command.ExecuteNonQueryAsync(ct);
        return updated;
    }

    private static async Task RetireActiveAsync(NpgsqlConnection connection, string name, DateTimeOffset now, CancellationToken ct)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT document::text FROM quality_execution_policy_revisions WHERE name=$1 AND status='Active' FOR UPDATE";
        select.Parameters.AddWithValue(name);
        var active = new List<ExecutionPolicyRevision>();
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) active.Add(Deserialize(reader.GetString(0)));
        foreach (var revision in active)
        {
            var retired = revision with { Status = ExecutionPolicyStatus.Retired, UpdatedAt = now };
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE quality_execution_policy_revisions SET status='Retired', updated_at=$3, document=$4::jsonb
                WHERE name=$1 AND version=$2
                """;
            update.Parameters.AddWithValue(retired.Name);
            update.Parameters.AddWithValue(retired.Version);
            update.Parameters.AddWithValue(retired.UpdatedAt);
            update.Parameters.AddWithValue(JsonSerializer.Serialize(retired, ContractJson.Options));
            await update.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task LockNameAsync(NpgsqlConnection connection, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 917))";
        command.Parameters.AddWithValue(name);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ExecutionPolicyRevision?> GetForUpdateAsync(NpgsqlConnection connection,
        string name, string version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document::text FROM quality_execution_policy_revisions WHERE name=$1 AND version=$2 FOR UPDATE
            """;
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(version);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }

    private static void Bind(NpgsqlCommand command, ExecutionPolicyRevision revision)
    {
        command.Parameters.AddWithValue(revision.Name);
        command.Parameters.AddWithValue(revision.Version);
        command.Parameters.AddWithValue(revision.Fingerprint);
        command.Parameters.AddWithValue(revision.Status.ToString());
        command.Parameters.AddWithValue(revision.CreatedAt);
        command.Parameters.AddWithValue(revision.UpdatedAt);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(revision, ContractJson.Options));
    }

    private static ExecutionPolicyRevision Deserialize(string json)
        => JsonSerializer.Deserialize<ExecutionPolicyRevision>(json, ContractJson.Options)
            ?? throw new InvalidOperationException("Execution policy revision is invalid");
}
