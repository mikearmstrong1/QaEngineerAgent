using System.Text.Json;
using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

public sealed class PostgresAutomationWorkflowStore(NpgsqlDataSource dataSource) : IAutomationWorkflowStore
{
    public async Task<AutomationWorkflow> CreateOrGetAsync(AutomationWorkflow workflow, CancellationToken ct)
    {
        await using var insert = dataSource.CreateCommand("""
            INSERT INTO quality_automation_workflows
                (id, job_id, idempotency_key_hash, status, revision, created_at, next_attempt_at, document)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8::jsonb)
            ON CONFLICT (idempotency_key_hash) DO NOTHING
            """);
        BindCreate(insert, workflow);
        if (await insert.ExecuteNonQueryAsync(ct) == 1) return workflow;
        await using var lookup = dataSource.CreateCommand(
            "SELECT document::text FROM quality_automation_workflows WHERE idempotency_key_hash=$1");
        lookup.Parameters.AddWithValue(workflow.IdempotencyKeyHash);
        var existing = await lookup.ExecuteScalarAsync(ct) is string json ? Deserialize(json)
            : throw new InvalidOperationException("Idempotent automation workflow disappeared");
        if (existing.InputHash != workflow.InputHash) throw new IdempotencyConflictException();
        return existing;
    }

    public async Task<AutomationWorkflow?> GetAsync(string id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT document::text FROM quality_automation_workflows WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }

    public async Task<IReadOnlyList<AutomationWorkflow>> ListByJobAsync(string jobId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT document::text FROM quality_automation_workflows
            WHERE job_id=$1 ORDER BY created_at DESC, id DESC
            """);
        command.Parameters.AddWithValue(jobId);
        var items = new List<AutomationWorkflow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Deserialize(reader.GetString(0)));
        return items;
    }

    public async Task<AutomationWorkflow?> ClaimAsync(TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT document::text, clock_timestamp() FROM quality_automation_workflows
            WHERE status NOT IN ('AwaitingReview','Completed','Failed','Cancelled')
              AND (lease_until IS NULL OR lease_until <= now())
              AND (next_attempt_at IS NULL OR next_attempt_at <= now())
            ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """;
        AutomationWorkflow current;
        DateTimeOffset now;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            current = Deserialize(reader.GetString(0));
            now = new DateTimeOffset(reader.GetDateTime(1));
        }
        var claimed = current with { LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease,
            StageAttempts = current.StageAttempts + 1, UpdatedAt = now, Revision = current.Revision + 1 };
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE quality_automation_workflows SET revision=$2, lease_token=$3, lease_until=$4,
                status=$5, next_attempt_at=$6, document=$7::jsonb WHERE id=$1 AND revision=$8
            """;
        BindUpdate(update, claimed, current.Revision);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new AutomationWorkflowConflictException();
        await tx.CommitAsync(ct);
        return claimed;
    }

    public async Task<AutomationWorkflow> SaveAsync(AutomationWorkflow workflow, long expectedRevision,
        string leaseToken, CancellationToken ct)
    {
        var saved = workflow with { Revision = expectedRevision + 1, LeaseToken = null, LeaseUntil = null };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_automation_workflows SET revision=$2, lease_token=NULL, lease_until=NULL,
                status=$3, next_attempt_at=$4, document=$5::jsonb
            WHERE id=$1 AND revision=$6 AND lease_token=$7 AND lease_until > now()
            """);
        command.Parameters.AddWithValue(saved.Id);
        command.Parameters.AddWithValue(saved.Revision);
        command.Parameters.AddWithValue(saved.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = (object?)saved.NextAttemptAt ?? DBNull.Value });
        command.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
        command.Parameters.AddWithValue(leaseToken);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomationWorkflowConflictException();
        return saved;
    }

    public async Task<AutomationWorkflow> SaveReviewAsync(AutomationWorkflow workflow, long expectedRevision, CancellationToken ct)
    {
        var saved = workflow with { Revision = expectedRevision + 1, LeaseToken = null, LeaseUntil = null };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_automation_workflows SET revision=$2, status=$3, next_attempt_at=$4,
                lease_token=NULL, lease_until=NULL, document=$5::jsonb
            WHERE id=$1 AND revision=$6 AND status='AwaitingReview'
            """);
        command.Parameters.AddWithValue(saved.Id);
        command.Parameters.AddWithValue(saved.Revision);
        command.Parameters.AddWithValue(saved.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = (object?)saved.NextAttemptAt ?? DBNull.Value });
        command.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomationWorkflowConflictException();
        return saved;
    }

    public async Task<AutomationWorkflow?> CancelAsync(string id, DateTimeOffset ignored, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT document::text, clock_timestamp() FROM quality_automation_workflows WHERE id=$1 FOR UPDATE";
        select.Parameters.AddWithValue(id);
        AutomationWorkflow current;
        DateTimeOffset now;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            current = Deserialize(reader.GetString(0));
            now = new DateTimeOffset(reader.GetDateTime(1));
        }
        if (current.IsTerminal) return current;
        var cancelled = current with { Status = AutomationWorkflowStatus.Cancelled, UpdatedAt = now,
            Error = "automation_cancelled", LeaseToken = null, LeaseUntil = null, NextAttemptAt = null,
            Revision = current.Revision + 1, Checkpoints = [.. current.Checkpoints,
                new(AutomationWorkflowStatus.Cancelled, now, $"cancel:{current.Revision + 1}")] };
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE quality_automation_workflows SET revision=$2, lease_token=NULL, lease_until=NULL,
                status='Cancelled', next_attempt_at=NULL, document=$3::jsonb WHERE id=$1
            """;
        update.Parameters.AddWithValue(cancelled.Id);
        update.Parameters.AddWithValue(cancelled.Revision);
        update.Parameters.AddWithValue(JsonSerializer.Serialize(cancelled, ContractJson.Options));
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return cancelled;
    }

    private static void BindCreate(NpgsqlCommand command, AutomationWorkflow workflow)
    {
        command.Parameters.AddWithValue(workflow.Id);
        command.Parameters.AddWithValue(workflow.JobId);
        command.Parameters.AddWithValue(workflow.IdempotencyKeyHash);
        command.Parameters.AddWithValue(workflow.Status.ToString());
        command.Parameters.AddWithValue(workflow.Revision);
        command.Parameters.AddWithValue(workflow.CreatedAt);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = (object?)workflow.NextAttemptAt ?? DBNull.Value });
        command.Parameters.AddWithValue(JsonSerializer.Serialize(workflow, ContractJson.Options));
    }
    private static void BindUpdate(NpgsqlCommand command, AutomationWorkflow workflow, long expectedRevision)
    {
        command.Parameters.AddWithValue(workflow.Id);
        command.Parameters.AddWithValue(workflow.Revision);
        command.Parameters.AddWithValue(workflow.LeaseToken!);
        command.Parameters.AddWithValue(workflow.LeaseUntil!.Value);
        command.Parameters.AddWithValue(workflow.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = (object?)workflow.NextAttemptAt ?? DBNull.Value });
        command.Parameters.AddWithValue(JsonSerializer.Serialize(workflow, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
    }
    private static AutomationWorkflow Deserialize(string json)
        => JsonSerializer.Deserialize<AutomationWorkflow>(json, ContractJson.Options)
            ?? throw new InvalidOperationException("Automation workflow is invalid");
}
