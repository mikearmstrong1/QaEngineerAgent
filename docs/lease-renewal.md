# Worker lease renewal

A claimed planning job now renews its ownership while it runs, including during requirement imports and planning calls. Defaults remain a five-minute lease, renewed every minute, with a 15-second renewal timeout. Renewal starts for each claimed attempt and stops when that attempt completes, fails, is cancelled, or exits with an infrastructure error.

## Ownership and persistence

Every renewal checks the persisted job ID, lease token, revision, nonterminal status, and unexpired lease. It extends the expiry without shortening it, increments the revision, and preserves the stored checkpoint and provider-operation records. An expired, replaced, or completed claim cannot be renewed.

A per-attempt gate coordinates renewal and checkpoint writes. Checkpoints use the revision and expiry returned by the latest successful renewal; they cannot accidentally overwrite that renewal with an older provider-call snapshot. Renewal does not add job transitions or decisions. The API's diagnostic revision and lease expiry can therefore change while job status stays the same.

The file store uses its existing process-shared file lock and atomic replacement. PostgreSQL locks the job row, reads database time **after** acquiring that lock, then updates expiry, revision, and the job document in one transaction. A renewal that waits behind a lock cannot revive an expired lease. No new database migration is required.

## Renewal failure and shutdown

A lost lease raises `LeaseLostException`. A renewal storage error or timeout raises a sanitized `LeaseRenewalException`. Both cancel processing and stop later checkpoint writes; they do not label the provider as failed or erase its saved operation record. The renewal timeout includes waiting for an in-flight checkpoint to release the gate.

The caller can stop waiting even if a provider ignores cancellation. A late provider result cannot initiate a new checkpoint after cancellation. Cancellation cannot retract a request already accepted by an external service or a storage commit already in flight. An uncertain renewal acknowledgement can also leave a longer lease persisted; a replacement worker waits until that persisted lease expires.

After expiry, [provider-operation recovery](provider-recovery.md) handles the saved state: read-only imports can repeat, saved results are reused, and uncertain planning calls require review. Shutdown does not release ownership early, because a cancelled provider call may still be finishing remotely.

## Configuration

All values are seconds; API, embedded worker, standalone worker, and CLI use the same options.

| Native environment setting | Default | Compose `.env` setting |
| --- | --- | --- |
| `Quality__Lease__DurationSeconds` | 300 | `QUALITY_LEASE_DURATION_SECONDS` |
| `Quality__Lease__RenewalIntervalSeconds` | 60 | `QUALITY_LEASE_RENEWAL_INTERVAL_SECONDS` |
| `Quality__Lease__RenewalTimeoutSeconds` | 15 | `QUALITY_LEASE_RENEWAL_TIMEOUT_SECONDS` |

Values must be positive. Duration is capped at one day, and renewal interval plus timeout must be strictly shorter than duration. Invalid settings fail startup before jobs are processed. Keep substantial headroom for scheduling and storage latency. Use consistent settings across roles.

Renewal protects ownership and does not extend provider deadlines. [Durable retry budgets](retry-budgets.md) now bound worker/import attempts and total processing time. Existing remote-provider timeouts and the CLI's two-minute wait remain in effect. This change applies to requirement-to-plan jobs; standalone reviewed browser execution has its separate timeout and result workflow.

## Verification

`LeaseRenewalTests` verifies long normalization and planning calls beyond their original lease, no competing claim while renewal is healthy, preserved checkpoints, revision fencing, rejection of expired/wrong-owner/terminal renewals, and stopping renewal after completion. It also covers renewal errors, lost ownership, lost acknowledgements, timeouts, blocked checkpoint writes, invalid settings, and a provider that ignores cancellation.

PostgreSQL tests use temporary schemas, exercise both long provider stages, and check expiry after a row-lock wait. They require `QUALITY_TEST_POSTGRES` and clean up their schemas afterward. The existing provider-recovery suite continues to cover interrupted jobs and late results from a superseded worker.

Verified on 2026-09-16:

- Release build passed with zero warnings/errors; TypeScript build passed.
- 120 .NET tests passed, including real PostgreSQL tests in temporary schemas. One unrelated MinIO integration test was skipped; the older shared-database pipeline test was excluded to avoid changing the running stack's schema.
- All four browser/API tests and isolated CLI replay/conflict checks passed.
- Custom lease settings completed a CLI job; invalid settings were rejected before creating another job.
- Compose configuration validation and whitespace checks passed.

The running application containers were not rebuilt or redeployed. No live provider calls were required.

Explicit [job cancellation](job-cancellation.md) is terminal and revokes ownership. It stops retries and preserves saved results; worker shutdown remains a recoverable interruption.
