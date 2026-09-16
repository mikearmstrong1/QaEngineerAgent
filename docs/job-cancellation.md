# Job cancellation

Cancel a job with `POST /jobs/{id}/cancel` using the configured bearer key, or with `Quality.Api cancel --id <id>` against the same store. Only requirement-to-plan jobs are cancellable; separate TestRuns retain their existing execution workflow.

## HTTP and CLI

The endpoint needs no request body and returns the job snapshot on success.

| HTTP status | Meaning |
| --- | --- |
| 200 | Cancelled now, or already Cancelled |
| 400 | Malformed job ID |
| 401 | Missing or invalid credentials when authentication is enabled |
| 404 | Job does not exist |
| 409 | Already Completed or Failed; response includes `job_already_terminal` and the unchanged job |

`cancel` prints the job JSON and exits with `0` for cancellation (including repeats), `4` for an already completed/failed job, `3` for a missing job, `2` for invalid arguments, or `1` for an infrastructure failure. `run` and `get` return `1` for a Cancelled job. The TypeScript client exposes `quality.cancel(id)`.

## Durable behavior

Cancellation atomically sets terminal `Cancelled`, records `job_cancelled`, appends one transition, increments revision, and clears lease ownership. Repeated cancellation leaves the snapshot unchanged. File storage uses its shared lock; PostgreSQL locks the row and updates the indexed fields and document in one transaction. Cancelled jobs cannot be claimed again, and stale worker saves and renewals are rejected immediately after cancellation commits.

Completion, failure, and cancellation compete for the same stored state: the first terminal commit wins. Saved requirements, plans, decisions, and retry-budget usage remain available. An in-flight Normalize operation becomes `Cancelled`; an in-flight Plan becomes `OutcomeUnknown` with `provider_operation_outcome_unknown`, because a remote provider may already have accepted the request. Completed provider operations stay completed.

Workers poll durable state every second by default and cancel the provider's token when they observe cancellation. Detection time includes scheduling and store latency; reads are bounded by the lease renewal timeout. A failed or timed-out cancellation check stops that attempt with a sanitized `JobMonitoringException`, retaining its recovery checkpoint. A provider that ignores its token cannot save a late result. Cancellation cannot undo a request already accepted by an external provider.

Reusing the same submission idempotency key returns the cancelled job. Submit with a new key only when intentionally starting a new job, after reviewing any uncertain external planning outcome. There is no automatic replay or resume of cancelled jobs. Ctrl-C and worker shutdown remain recoverable interruptions; use the explicit cancel command/endpoint to stop a job durably.

## Configuration and upgrade

`Quality__Lease__CancellationPollIntervalSeconds` defaults to `1`; Compose exposes `QUALITY_LEASE_CANCELLATION_POLL_INTERVAL_SECONDS`. Values must be positive and no greater than 60 seconds. All worker roles use this setting. Polling reads use `Quality__Lease__RenewalTimeoutSeconds` (default 15 seconds).

Migration 3 updates the pending-job index to exclude Cancelled jobs. Stop/drain older API and worker binaries before upgrading all roles: older binaries do not understand the new terminal status. Existing jobs remain readable by the new application. Do not run a mixed-version deployment.

## Verification

`JobCancellationTests` covers both stores, concurrent and repeated cancellation, terminal conflicts, idempotent replay, lease revocation, active imports/planning, ignored tokens, late results, retry backoff, saved-plan preservation, deadline races, and monitor failures. PostgreSQL tests use isolated temporary schemas. `scripts/verify-auth.py` verifies authenticated HTTP/CLI cancellation with temporary storage and validates the cancelled response against the JSON schema. Browser checks verify terminal conflicts and input errors.

Verified on 2026-09-16:

- Full .NET suite: 151 passed, one unrelated MinIO test skipped; the old shared-database test was excluded to preserve the running stack. The expanded 13-test cancellation suite also passed after adding deadline and concurrent claim/cancel coverage.
- Release build passed with no warnings/errors; TypeScript build passed.
- All five browser/API smoke tests passed.
- Isolated authenticated HTTP/CLI cancellation, schema validation, and CLI submission replay checks passed.
- Compose configuration and whitespace checks passed.

The running application containers were not rebuilt or deployed. PostgreSQL verification used temporary schemas; provider tests used local fakes.
