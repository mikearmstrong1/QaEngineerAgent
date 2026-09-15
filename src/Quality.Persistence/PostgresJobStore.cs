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
    public async Task<QualityJob?> GetAsync(string id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT document::text FROM quality_jobs WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }
    public async Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT document::text, now() FROM quality_jobs
            WHERE status NOT IN ('Completed','Failed') AND (lease_until IS NULL OR lease_until <= now())
            AND ($1::text IS NULL OR id=$1) ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """;
        select.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)id ?? DBNull.Value });
        QualityJob job;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            job = Deserialize(reader.GetString(0));
            job = job with { Revision = job.Revision + 1, LeaseToken = Guid.NewGuid().ToString("N"),
                LeaseUntil = new DateTimeOffset(reader.GetDateTime(1)) + lease };
        }
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE quality_jobs SET revision=$2, lease_token=$3, lease_until=$4, document=$5::jsonb WHERE id=$1";
        Bind(update, job);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return job;
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
