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
        """, """
        CREATE UNIQUE INDEX ux_quality_jobs_submission_key
            ON quality_jobs ((document->>'submissionKeyHash'))
            WHERE document->>'submissionKeyHash' IS NOT NULL;
        """, """
        DROP INDEX IF EXISTS ix_quality_jobs_pending;
        CREATE INDEX ix_quality_jobs_pending ON quality_jobs(created_at)
            WHERE status NOT IN ('Completed', 'Failed', 'Cancelled');
        """, """
        CREATE TABLE IF NOT EXISTS quality_execution_requests (
            id text PRIMARY KEY,
            job_id text NOT NULL REFERENCES quality_jobs(id),
            created_at timestamptz NOT NULL,
            status text NOT NULL,
            revision bigint NOT NULL,
            lease_token text NULL,
            lease_until timestamptz NULL,
            document jsonb NOT NULL
        );
        CREATE INDEX ix_quality_execution_requests_job
            ON quality_execution_requests(job_id, created_at DESC);
        CREATE TABLE IF NOT EXISTS quality_test_runs (
            id text PRIMARY KEY,
            test_plan_id text NOT NULL,
            started_at timestamptz NOT NULL,
            status text NOT NULL,
            document jsonb NOT NULL
        );
        CREATE INDEX ix_quality_test_runs_plan
            ON quality_test_runs(test_plan_id, started_at DESC);
        """, """
        CREATE TABLE IF NOT EXISTS quality_execution_policy_revisions (
            name text NOT NULL,
            version text NOT NULL,
            fingerprint text NOT NULL,
            status text NOT NULL CHECK (status IN ('Draft', 'Active', 'Disabled', 'Retired')),
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL,
            document jsonb NOT NULL,
            PRIMARY KEY (name, version),
            UNIQUE (fingerprint)
        );
        CREATE UNIQUE INDEX ux_quality_execution_policy_active
            ON quality_execution_policy_revisions(name) WHERE status='Active';
        """, """
        CREATE TABLE quality_automation_workflows (
            id text PRIMARY KEY,
            job_id text NOT NULL REFERENCES quality_jobs(id),
            idempotency_key_hash text NOT NULL UNIQUE,
            status text NOT NULL CHECK (status IN
                ('Triggered','Planned','Inspected','ManifestPrepared','PolicyEvaluated','AwaitingReview',
                 'Approved','Queued','Executed','EvidencePublished','Classified','Completed','Failed','Cancelled')),
            revision bigint NOT NULL,
            lease_token text NULL,
            lease_until timestamptz NULL,
            next_attempt_at timestamptz NULL,
            created_at timestamptz NOT NULL,
            document jsonb NOT NULL
        );
        CREATE INDEX ix_quality_automation_workflows_job
            ON quality_automation_workflows(job_id, created_at DESC);
        CREATE INDEX ix_quality_automation_workflows_claim
            ON quality_automation_workflows(created_at)
            WHERE status NOT IN ('AwaitingReview','Completed','Failed','Cancelled');
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
