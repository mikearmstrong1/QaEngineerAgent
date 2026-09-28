using System.Text.Json;
using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

public sealed class PostgresAutomationScheduleStore(NpgsqlDataSource dataSource) : IAutomationScheduleStore
{
    public Task InitializeAsync(CancellationToken ct) => PostgresMigrations.ApplyAsync(dataSource, ct);

    public async Task<AutomationSchedule> CreateAsync(AutomationSchedule schedule, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO quality_automation_schedules
                (id,name,enabled,next_occurrence_at,revision,document)
            VALUES ($1,$2,$3,$4,$5,$6::jsonb)
            ON CONFLICT DO NOTHING
            """);
        Bind(command, schedule);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new ArgumentException("An automation schedule with that name already exists");
        return schedule;
    }

    public async Task<AutomationSchedule?> GetAsync(string id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT document::text FROM quality_automation_schedules WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }

    public async Task<IReadOnlyList<AutomationSchedule>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT document::text FROM quality_automation_schedules ORDER BY lower(name), id");
        var result = new List<AutomationSchedule>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Deserialize(reader.GetString(0)));
        return result;
    }

    public async Task<AutomationSchedule> SetEnabledAsync(string id, long expectedRevision, bool enabled,
        DateTimeOffset now, CancellationToken ct)
    {
        var current = await GetAsync(id, ct) ?? throw new ArgumentException("Automation schedule not found");
        if (current.Revision != expectedRevision) throw new AutomationScheduleConflictException();
        var saved = current with { Enabled = enabled, UpdatedAt = now, Revision = current.Revision + 1,
            LeaseToken = null, LeaseUntil = null, ClaimedOccurrenceAt = null };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_automation_schedules SET enabled=$2, revision=$3, lease_token=NULL,
                lease_until=NULL, document=$4::jsonb WHERE id=$1 AND revision=$5
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(enabled);
        command.Parameters.AddWithValue(saved.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomationScheduleConflictException();
        return saved;
    }

    public async Task<AutomationSchedule?> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT document::text FROM quality_automation_schedules
            WHERE enabled AND next_occurrence_at <= $1 AND (lease_until IS NULL OR lease_until <= $1)
            ORDER BY next_occurrence_at, id FOR UPDATE SKIP LOCKED LIMIT 1
            """;
        select.Parameters.AddWithValue(now);
        var json = await select.ExecuteScalarAsync(ct) as string;
        if (json is null) return null;
        var current = Deserialize(json);
        var claimed = current with { LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease,
            ClaimedOccurrenceAt = current.NextOccurrenceAt, UpdatedAt = now, Revision = current.Revision + 1 };
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE quality_automation_schedules SET revision=$2, lease_token=$3, lease_until=$4,
                document=$5::jsonb WHERE id=$1 AND revision=$6
            """;
        update.Parameters.AddWithValue(claimed.Id);
        update.Parameters.AddWithValue(claimed.Revision);
        update.Parameters.AddWithValue(claimed.LeaseToken);
        update.Parameters.AddWithValue(claimed.LeaseUntil.Value);
        update.Parameters.AddWithValue(JsonSerializer.Serialize(claimed, ContractJson.Options));
        update.Parameters.AddWithValue(current.Revision);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new AutomationScheduleConflictException();
        await transaction.CommitAsync(ct);
        return claimed;
    }

    public async Task<AutomationSchedule> CompleteClaimAsync(AutomationSchedule schedule, long expectedRevision,
        string leaseToken, string workflowId, DateTimeOffset now, CancellationToken ct)
    {
        if (schedule.ClaimedOccurrenceAt is null) throw new AutomationScheduleConflictException();
        var saved = schedule with
        {
            LastOccurrenceAt = schedule.ClaimedOccurrenceAt,
            LastWorkflowId = workflowId,
            NextOccurrenceAt = AutomationScheduleService.NextAfter(schedule.ClaimedOccurrenceAt.Value,
                schedule.IntervalSeconds, now),
            LeaseToken = null,
            LeaseUntil = null,
            ClaimedOccurrenceAt = null,
            UpdatedAt = now,
            Revision = expectedRevision + 1
        };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_automation_schedules SET revision=$2, lease_token=NULL, lease_until=NULL,
                next_occurrence_at=$3, document=$4::jsonb
            WHERE id=$1 AND revision=$5 AND lease_token=$6 AND lease_until > $7
            """);
        command.Parameters.AddWithValue(saved.Id);
        command.Parameters.AddWithValue(saved.Revision);
        command.Parameters.AddWithValue(saved.NextOccurrenceAt);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(now);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomationScheduleConflictException();
        return saved;
    }

    public async Task ReleaseClaimAsync(string id, long expectedRevision, string leaseToken, CancellationToken ct)
    {
        var current = await GetAsync(id, ct);
        if (current is null || current.Revision != expectedRevision || current.LeaseToken != leaseToken) return;
        var saved = current with { LeaseToken = null, LeaseUntil = null, ClaimedOccurrenceAt = null,
            Revision = current.Revision + 1 };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_automation_schedules SET revision=$2, lease_token=NULL, lease_until=NULL,
                document=$3::jsonb WHERE id=$1 AND revision=$4 AND lease_token=$5
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(saved.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(saved, ContractJson.Options));
        command.Parameters.AddWithValue(expectedRevision);
        command.Parameters.AddWithValue(leaseToken);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(NpgsqlCommand command, AutomationSchedule schedule)
    {
        command.Parameters.AddWithValue(schedule.Id);
        command.Parameters.AddWithValue(schedule.Name);
        command.Parameters.AddWithValue(schedule.Enabled);
        command.Parameters.AddWithValue(schedule.NextOccurrenceAt);
        command.Parameters.AddWithValue(schedule.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(schedule, ContractJson.Options));
    }

    private static AutomationSchedule Deserialize(string json)
        => JsonSerializer.Deserialize<AutomationSchedule>(json, ContractJson.Options)
            ?? throw new InvalidOperationException("Automation schedule is invalid");
}
