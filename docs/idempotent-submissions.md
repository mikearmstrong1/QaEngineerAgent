# Idempotent job submissions

Send an optional `Idempotency-Key` header to `POST /jobs`. The TypeScript client accepts it as the second argument to `submit(reference, key)`. The CLI accepts `run --source jira --reference AUTH-1427 --idempotency-key <key>`.

- Same key and identical source/id: returns the existing job, its current status, and the same Location with HTTP 202. Completed and failed jobs are replayed without restarting processing.
- Same key with a different source/id: HTTP 409 with `idempotency_key_conflict`.
- Missing key: creates a new job each time, preserving the original behavior.
- Keys are case-sensitive, 1–200 printable ASCII characters without spaces. Invalid keys return HTTP 400. Use a fresh random key for each intentional new operation and keep it for retries, including after an uncertain network response.

Keys apply across the shared job store, including API and CLI clients; they are not scoped to an API key. Only the SHA-256 key hash is persisted in `submissionKeyHash`. No expiration is imposed: protection lasts while the job is retained. Removing the job removes its replay protection. Identical requests with different keys intentionally produce different jobs; changed upstream requirements require a new key to request a fresh plan.

The file store searches persisted jobs under its existing cross-process lock and atomically writes the job and key hash in one document. This scan is appropriate for the local fallback; use PostgreSQL for larger stores. PostgreSQL migration 2 adds a unique partial index on the persisted key hash. Conflicting inserts wait for the winner, then read the committed job in a separate statement. Existing jobs require no backfill.

This prevents duplicate provider work caused by duplicate submissions. It does not guarantee exactly-once external provider calls after a worker crashes between receiving a provider response and persisting it, or when a processing lease expires. The [provider-operation journal](provider-recovery.md) now reuses saved results and stops automatic replay of uncertain planning calls. [Lease renewal](lease-renewal.md) now maintains ownership for active workers and stops processing when renewal fails.

## Verification

Tests cover concurrent submissions through separate store instances, durable replay after completion, key conflicts, invalid keys, and intentional fresh requests. PostgreSQL checks use temporary schemas and remove them after completion. HTTP tests cover simultaneous retries, completed-job replay, and 400/409 responses. The CLI supports the same shared store semantics.

Run `python3 scripts/verify-idempotency.py` after a Release build to verify CLI replay and conflicts in a temporary file store.

Verified on 2026-09-15: Release build with zero warnings/errors; 87 .NET tests passed (six environment-dependent tests skipped in the default run); all five targeted PostgreSQL/idempotency/migration checks passed against real PostgreSQL in isolated schemas; the expanded claimed/failed-job replay tests passed for both stores; TypeScript build, all four browser/HTTP tests, and isolated CLI verification passed. The running Compose application image was not rebuilt or redeployed.
