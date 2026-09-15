# Database migrations

PostgreSQL API and worker startup apply ordered migrations before accepting work. The file store is unchanged.

`PostgresMigrations` owns an append-only list of SQL scripts. The database records each version, SHA-256 checksum, and application timestamp in `quality_schema_migrations`. Startup takes the existing transaction-scoped advisory lock, validates the recorded history, and applies pending scripts and ledger entries in one transaction. Concurrent API and worker startup is serialized. A failed script rolls back all changes from that startup attempt.

Migration 1 adopts the existing `quality_jobs` table and pending-job index without deleting jobs. It also supports a fresh database. Existing tables must match the original application schema; this is not an automatic repair tool for manually altered schemas.

Add future migrations at the end of the script list. Never edit an applied script. Startup rejects changed checksums, gaps, and versions newer than the running application. Do not erase ledger entries to bypass these checks: restore the matching application release or investigate the database change. There are no automatic down migrations; back up the database before deployment and plan recovery for each schema change.

Verification uses unique temporary schemas in the dedicated `QUALITY_TEST_POSTGRES` database. Tests cover concurrent startup and legacy-job preservation, altered/newer/noncontiguous history, and rollback after SQL failure. Run `dotnet test` with that connection string set. Without it these integration tests are explicitly skipped.

API authentication and the remaining operational controls are still pending. This migration change alone does not make the service ready for public deployment.
