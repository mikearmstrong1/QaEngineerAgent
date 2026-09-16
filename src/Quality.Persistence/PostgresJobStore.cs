using System.Text.Json;
using Npgsql;
using Quality.Domain;
using Quality.Orchestrator;
namespace Quality.Persistence;

public sealed class PostgresJobStore(NpgsqlDataSource dataSource) : IJobStore
{
    public Task InitializeAsync(CancellationToken ct) => PostgresMigrations.ApplyAsync(dataSource, ct);
    public async Task CreateAsync(QualityJob job, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO quality_jobs(id, created_at, status, revision, document)
            VALUES ($1, $2, $3, $4, $5::jsonb)
            """);
        command.Parameters.AddWithValue(job.Id);
        command.Parameters.AddWithValue(job.CreatedAt);
        command.Parameters.AddWithValue(job.Status.ToString());
        command.Parameters.AddWithValue(job.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(job, ContractJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<QualityJob> CreateOrGetAsync(QualityJob job, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(job.SubmissionKeyHash);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO quality_jobs(id, created_at, status, revision, document)
            VALUES ($1, $2, $3, $4, $5::jsonb)
            ON CONFLICT ((document->>'submissionKeyHash'))
                WHERE document->>'submissionKeyHash' IS NOT NULL DO NOTHING
            """);
        command.Parameters.AddWithValue(job.Id);
        command.Parameters.AddWithValue(job.CreatedAt);
        command.Parameters.AddWithValue(job.Status.ToString());
        command.Parameters.AddWithValue(job.Revision);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(job, ContractJson.Options));
        if (await command.ExecuteNonQueryAsync(ct) == 1) return job;
        // Separate statement gets a fresh snapshot after a concurrent insert commits.
        await using var lookup = dataSource.CreateCommand("SELECT document::text FROM quality_jobs WHERE document->>'submissionKeyHash'=$1");
        lookup.Parameters.AddWithValue(job.SubmissionKeyHash);
        return await lookup.ExecuteScalarAsync(ct) is string json ? Deserialize(json)
            : throw new InvalidOperationException("Idempotent submission disappeared");
    }
    public async Task<QualityJob?> GetAsync(string id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT document::text FROM quality_jobs WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }
    public async Task<QualityJob?> CancelAsync(string id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT document::text FROM quality_jobs WHERE id=$1 FOR UPDATE";
        select.Parameters.AddWithValue(id);
        if (await select.ExecuteScalarAsync(ct) is not string json) return null;
        var current = Deserialize(json);
        if (current.IsTerminal) return current;
        await using var time = connection.CreateCommand();
        time.CommandText = "SELECT clock_timestamp()";
        var now = new DateTimeOffset((DateTime)(await time.ExecuteScalarAsync(ct))!);
        var cancelled = current.Cancel(now) with { Revision = current.Revision + 1 };
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE quality_jobs SET revision=$2, lease_token=$3, lease_until=$4, document=$5::jsonb, status=$6 WHERE id=$1";
        Bind(update, cancelled);
        update.Parameters.AddWithValue(cancelled.Status.ToString());
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return cancelled;
    }
    public async Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT document::text, clock_timestamp() FROM quality_jobs
            WHERE status NOT IN ('Completed','Failed','Cancelled') AND (lease_until IS NULL OR lease_until <= now())
            AND ($1::text IS NULL OR id=$1) ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """;
        select.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)id ?? DBNull.Value });
        QualityJob job;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            job = Deserialize(reader.GetString(0));
            job = job.Claim(new DateTimeOffset(reader.GetDateTime(1)), lease);
        }
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE quality_jobs SET revision=$2, lease_token=$3, lease_until=$4, document=$5::jsonb, status=$6 WHERE id=$1";
        Bind(update, job);
        update.Parameters.AddWithValue(job.Status.ToString());
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return job;
    }
    public async Task<QualityJob> RenewLeaseAsync(QualityJob job, TimeSpan lease, CancellationToken ct)
    {
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT document::text FROM quality_jobs WHERE id=$1 FOR UPDATE";
        select.Parameters.AddWithValue(job.Id);
        if (await select.ExecuteScalarAsync(ct) is not string json) throw new LeaseLostException();
        var current = Deserialize(json);
        // Read database time after acquiring the row lock; lock waits must not revive an expired lease.
        await using var time = connection.CreateCommand();
        time.CommandText = "SELECT clock_timestamp()";
        var now = new DateTimeOffset((DateTime)(await time.ExecuteScalarAsync(ct))!);
        if (current.IsTerminal || current.Revision != job.Revision || current.LeaseToken is null ||
            current.LeaseToken != job.LeaseToken || current.LeaseUntil is null || current.LeaseUntil <= now)
            throw new LeaseLostException();
        var until = now + lease;
        current = current with { Revision = current.Revision + 1,
            LeaseUntil = until > current.LeaseUntil ? until : current.LeaseUntil };
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE quality_jobs SET revision=$2, lease_token=$3, lease_until=$4, document=$5::jsonb
            WHERE id=$1 AND lease_until > clock_timestamp()
            """;
        Bind(update, current);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new LeaseLostException();
        await tx.CommitAsync(ct);
        return current;
    }
    public async Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
    {
        var saved = job with { Revision = job.Revision + 1, LeaseToken = job.IsTerminal ? null : job.LeaseToken,
            LeaseUntil = job.IsTerminal ? null : job.LeaseUntil };
        await using var command = dataSource.CreateCommand("""
            UPDATE quality_jobs SET revision=$2, lease_token=$3, lease_until=$4, document=$5::jsonb, status=$6
            WHERE id=$1 AND revision=$7 AND lease_token=$8 AND lease_until > now()
            """);
        Bind(command, saved);
        command.Parameters.AddWithValue(saved.Status.ToString());
        command.Parameters.AddWithValue(job.Revision);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)job.LeaseToken ?? DBNull.Value });
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new LeaseLostException();
        return saved;
    }
    private static void Bind(NpgsqlCommand command, QualityJob job)
    {
        command.Parameters.AddWithValue(job.Id);
        command.Parameters.AddWithValue(job.Revision);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)job.LeaseToken ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = (object?)job.LeaseUntil ?? DBNull.Value });
        command.Parameters.AddWithValue(JsonSerializer.Serialize(job, ContractJson.Options));
    }
    private static QualityJob Deserialize(string json) => JsonSerializer.Deserialize<QualityJob>(json, ContractJson.Options)!;
}
