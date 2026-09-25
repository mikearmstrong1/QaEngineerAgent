# Database migrations

PostgreSQL API and worker startup apply ordered migrations before accepting work. The file store is unchanged.

`PostgresMigrations` owns an append-only list of SQL scripts. The database records each version, SHA-256 checksum, and application timestamp in `quality_schema_migrations`. Startup takes the existing transaction-scoped advisory lock, validates the recorded history, and applies pending scripts and ledger entries in one transaction. Concurrent API and worker startup is serialized. A failed script rolls back all changes from that startup attempt.

Migration 1 adopts the existing `quality_jobs` table and pending-job index without deleting jobs. It also supports a fresh database. Existing tables must match the original application schema; this is not an automatic repair tool for manually altered schemas.

Migration 2 adds a unique partial index on the job document’s submission key hash for atomic idempotent submissions. Existing unkeyed jobs remain valid.

Migration 3 rebuilds the pending-job index to exclude `Cancelled` jobs. Stop/drain old API and worker binaries before upgrading all roles; old binaries cannot read the new status. See [job cancellation](job-cancellation.md).

Migration 4 adds durable execution-request and test-run tables and their job/plan indexes.

Migration 5 adds immutable execution-policy revisions with a `(name, version)` primary key, fingerprint uniqueness, constrained lifecycle states, and a partial unique index enforcing at most one active revision per policy name.

Migration 6 adds durable automation workflows with idempotent trigger keys, leased stage processing, retry scheduling, lifecycle constraints, and job/claim indexes.

Add future migrations at the end of the script list. Never edit an applied script. Startup rejects changed checksums, gaps, and versions newer than the running application. Do not erase ledger entries to bypass these checks: restore the matching application release or investigate the database change. There are no automatic down migrations; back up the database before deployment and plan recovery for each schema change.

Verification uses unique temporary schemas in the dedicated `QUALITY_TEST_POSTGRES` database. Tests cover concurrent startup and legacy-job preservation, altered/newer/noncontiguous history, and rollback after SQL failure. Run `dotnet test` with that connection string set. Without it these integration tests are explicitly skipped.

API authentication and idempotent submissions are implemented; further operational controls remain pending. This migration change alone does not make the service ready for public deployment.
