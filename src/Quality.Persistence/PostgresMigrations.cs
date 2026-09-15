using System.Security.Cryptography;
using System.Text;
using Npgsql;
namespace Quality.Persistence;

internal static class PostgresMigrations
{
    // Append migrations. Never change SQL that has already shipped.
    private static readonly string[] Scripts = ["""
        CREATE TABLE IF NOT EXISTS quality_jobs (
            id text PRIMARY KEY,
            created_at timestamptz NOT NULL,
            status text NOT NULL,
            revision bigint NOT NULL,
            lease_token text NULL,
            lease_until timestamptz NULL,
            document jsonb NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_quality_jobs_pending ON quality_jobs(created_at)
            WHERE status NOT IN ('Completed', 'Failed');
        """];

    public static async Task ApplyAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_advisory_xact_lock(71632001);
            CREATE TABLE IF NOT EXISTS quality_schema_migrations (
                version integer PRIMARY KEY CHECK (version > 0),
                sha256 text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT version, sha256 FROM quality_schema_migrations ORDER BY version";
        var applied = new List<(int Version, string Hash)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) applied.Add((reader.GetInt32(0), reader.GetString(1)));

        for (var index = 0; index < applied.Count; index++)
        {
            var entry = applied[index];
            if (entry.Version != index + 1 || entry.Version > Scripts.Length || entry.Hash != Hash(Scripts[index]))
                throw new InvalidOperationException("Database migration history is incompatible with this application release");
        }
        for (var index = applied.Count; index < Scripts.Length; index++)
        {
            command.Parameters.Clear();
            command.CommandText = Scripts[index];
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = "INSERT INTO quality_schema_migrations(version, sha256) VALUES ($1, $2)";
            command.Parameters.AddWithValue(index + 1);
            command.Parameters.AddWithValue(Hash(Scripts[index]));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static string Hash(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
}
