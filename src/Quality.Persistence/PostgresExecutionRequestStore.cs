using System.Text.Json;
using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

public sealed class PostgresExecutionRequestStore(NpgsqlDataSource dataSource) : IExecutionRequestStore
{
    public async Task CreateAsync(ExecutionRequest request, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO quality_execution_requests(id, job_id, created_at, status, revision, document)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb)
            """);
        Bind(command, request);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ExecutionRequest?> GetAsync(string id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT document::text FROM quality_execution_requests WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }

    public async Task<IReadOnlyList<ExecutionRequest>> ListByJobAsync(string jobId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT document::text FROM quality_execution_requests WHERE job_id=$1 ORDER BY created_at DESC, id DESC");
        command.Parameters.AddWithValue(jobId);
        var items = new List<ExecutionRequest>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Deserialize(reader.GetString(0)));
        return items;
    }

    public async Task<ExecutionRequest?> CancelAsync(string id, DateTimeOffset ignored, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT document::text, clock_timestamp() FROM quality_execution_requests WHERE id=$1 FOR UPDATE";
        select.Parameters.AddWithValue(id);
        ExecutionRequest current;
        DateTimeOffset now;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            current = Deserialize(reader.GetString(0));
            now = new DateTimeOffset(reader.GetDateTime(1));
        }
        if (current.Status is ExecutionRequestStatus.Passed or ExecutionRequestStatus.Failed
            or ExecutionRequestStatus.TimedOut or ExecutionRequestStatus.InfrastructureFailed or ExecutionRequestStatus.Cancelled)
            return current;
        var cancelled = current with { Status = ExecutionRequestStatus.Cancelled, UpdatedAt = now,
            Error = "automation_cancelled", LeaseToken = null, LeaseUntil = null, Revision = current.Revision + 1 };
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE quality_execution_requests SET status='Cancelled', revision=$2, lease_token=NULL,
                lease_until=NULL, document=$3::jsonb WHERE id=$1
            """;
        update.Parameters.AddWithValue(cancelled.Id);
        update.Parameters.AddWithValue(cancelled.Revision);
        update.Parameters.AddWithValue(JsonSerializer.Serialize(cancelled, ContractJson.Options));
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return cancelled;
    }

    public async Task<ExecutionRequest?> TryReserveAutoLaunchAsync(string id, long expectedRevision,
        AutoLaunchBudget budget, DateTimeOffset now, CancellationToken ct)
    {
        if (budget.PolicyHash.Length != 64 || budget.MaximumLifetime is < 1 or > 100
            || budget.MaximumConcurrent is < 1 or > 100 || budget.WindowSeconds is < 60 or > 86400
            || budget.MaximumInWindow is < 1 or > 1000)
            throw new ArgumentException("Automatic launch budget is invalid");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";
            lockCommand.Parameters.AddWithValue(budget.PolicyHash);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }
        ExecutionRequest current;
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT document::text, clock_timestamp() FROM quality_execution_requests WHERE id=$1 FOR UPDATE";
            select.Parameters.AddWithValue(id);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ExecutionRequestConflictException();
            current = Deserialize(reader.GetString(0));
            now = new DateTimeOffset(reader.GetDateTime(1));
        }
        if (current.Revision != expectedRevision) throw new ExecutionRequestConflictException();
        if (current.Status != ExecutionRequestStatus.Approved || current.AutomationPolicyHash != budget.PolicyHash)
            throw new ArgumentException("Only a policy-approved request can reserve an automatic launch");
        if (current.AutoLaunchReservedAt is not null) { await tx.CommitAsync(ct); return current; }
        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT count(*),
                   count(*) FILTER (WHERE status IN ('Approved', 'Queued', 'Running')),
                   count(*) FILTER (WHERE (document->>'autoLaunchReservedAt')::timestamptz >= $2)
            FROM quality_execution_requests
            WHERE document->>'automationPolicyHash'=$1
              AND document->>'autoLaunchReservedAt' IS NOT NULL
            """;
        count.Parameters.AddWithValue(budget.PolicyHash);
        count.Parameters.AddWithValue(now.AddSeconds(-budget.WindowSeconds));
        int lifetime, concurrent, inWindow;
        await using (var reader = await count.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            lifetime = checked((int)reader.GetInt64(0));
            concurrent = checked((int)reader.GetInt64(1));
            inWindow = checked((int)reader.GetInt64(2));
        }
        if (lifetime >= budget.MaximumLifetime || concurrent >= budget.MaximumConcurrent || inWindow >= budget.MaximumInWindow)
        {
            await tx.CommitAsync(ct);
            return null;
        }
        var saved = current with { AutoLaunchReservedAt = now, UpdatedAt = now, Revision = current.Revision + 1 };
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE quality_execution_requests SET revision=$2, document=$3::jsonb WHERE id=$1 AND revision=$4
            """;
        update.Parameters.AddWithValue(saved.Id);
        update.Parameters.AddWithValue(saved.Revision);
        update.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        update.Parameters.AddWithValue(expectedRevision);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new ExecutionRequestConflictException();
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<ExecutionRequest> SaveAsync(ExecutionRequest request, long expectedRevision, CancellationToken ct)
    {
        var saved = request with { Revision = expectedRevision + 1 };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_execution_requests SET status=$2, revision=$3, document=$4::jsonb
            WHERE id=$1 AND revision=$5
            """);
        command.Parameters.AddWithValue(saved.Id);
        command.Parameters.AddWithValue(saved.Status.ToString());
        command.Parameters.AddWithValue(saved.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new ExecutionRequestConflictException();
        return saved;
    }

    public async Task<ExecutionRequest?> ClaimAsync(TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT document::text, clock_timestamp() FROM quality_execution_requests
            WHERE status='Queued' OR (status='Running' AND lease_until <= now())
            ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """;
        ExecutionRequest current;
        DateTimeOffset now;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            current = Deserialize(reader.GetString(0));
            now = new DateTimeOffset(reader.GetDateTime(1));
        }
        var claimed = current.Status == ExecutionRequestStatus.Running
            ? current with { Status = ExecutionRequestStatus.InfrastructureFailed, Error = "execution_worker_interrupted",
                UpdatedAt = now, LeaseToken = null, LeaseUntil = null, Revision = current.Revision + 1 }
            : current with { Status = ExecutionRequestStatus.Running, UpdatedAt = now,
                LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease,
                Attempts = current.Attempts + 1, Revision = current.Revision + 1 };
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE quality_execution_requests SET status=$2, revision=$3, lease_token=$4, lease_until=$5, document=$6::jsonb WHERE id=$1";
        update.Parameters.AddWithValue(claimed.Id);
        update.Parameters.AddWithValue(claimed.Status.ToString());
        update.Parameters.AddWithValue(claimed.Revision);
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)claimed.LeaseToken ?? DBNull.Value });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = (object?)claimed.LeaseUntil ?? DBNull.Value });
        update.Parameters.AddWithValue(JsonSerializer.Serialize(claimed, ContractJson.Options));
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return claimed;
    }

    private static void Bind(NpgsqlCommand command, ExecutionRequest request)
    {
        command.Parameters.AddWithValue(request.Id);
        command.Parameters.AddWithValue(request.JobId);
        command.Parameters.AddWithValue(request.CreatedAt);
        command.Parameters.AddWithValue(request.Status.ToString());
        command.Parameters.AddWithValue(request.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(request, ContractJson.Options));
    }
    private static ExecutionRequest Deserialize(string json) => JsonSerializer.Deserialize<ExecutionRequest>(json, ContractJson.Options)!;
}
