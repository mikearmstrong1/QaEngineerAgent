# Durable retry budgets

New jobs snapshot their retry policy when submitted. That policy, worker-attempt usage, and processing deadline are persisted in the job's optional `retryBudget` field. Worker restarts, lease renewals, deployment configuration changes, and idempotent submission replay cannot reset them.

## Limits and behavior

| Limit | Default | What consumes it |
| --- | --- | --- |
| Worker attempts | 5 | Each successful atomic claim, including a claim followed by a crash before provider work |
| Requirement-import attempts | 3 | Each persisted `Normalize` operation start, including interrupted reads |
| Processing time | 900 seconds | Wall-clock time from the first claim, including provider calls, retry delays, downtime, and recovery waits |
| Initial retry delay | 250 milliseconds | Exponential delay before another import attempt |
| Maximum retry delay | 5000 milliseconds | Maximum permitted delay before another import attempt |

Counts include the original attempt. Claims increment worker usage under the existing file lock or PostgreSQL row lock. When a claim encounters an exhausted budget, it atomically marks the job Failed without starting another provider call or incrementing usage past the limit. Lease renewal changes ownership metadata but does not consume attempts. A fully unavailable store cannot persist exhaustion; the next successful claim enforces it.

The deadline starts on first claim, so time spent queued before any worker picks up the job is excluded. Active workers cancel processing at the remaining deadline and use the latest owned checkpoint to persist failure. The deadline does not release a live lease early. An abandoned job is marked failed when it becomes claimable again after lease expiry. Saved results are retained for inspection, even if the budget prevents another attempt from finishing their stage transition.

For PostgreSQL, the first deadline and subsequent claim times use database time. The active worker derives remaining duration from those two timestamps and conservatively subtracts claim round-trip time, avoiding a comparison between database and host UTC. Persisted retry-delay timestamps use worker UTC; keep worker clocks synchronized.

## Safe import retries

Only read-only normalization gets new application-level retries. Transport errors without an HTTP status, timeouts not caused by caller cancellation, and HTTP 408/429/500–599 are transient. Authentication, not-found, validation, parsing, and other permanent failures still fail immediately.

The next retry time is saved as `providerOperations[].retryAt`, with a sanitized `requirement_transient_failure` marker. Backoff doubles from the initial delay up to the maximum. A replacement worker honors the persisted retry time and increments the import count only when it commits the next invocation start. If a crash left only a started operation, recovery saves a delay before repeating the read. No jitter is applied in this version.

Jira/Coda `Retry-After` values are honored when longer than the calculated delay. A requested delay beyond the configured maximum exhausts the import budget rather than retrying earlier than the server requested. A delay within that maximum may still exhaust the overall processing deadline. Cancellation and lease loss do not get retried within the current attempt.

## Planning and uncertain outcomes

The existing planning adapter retains its own HTTP-attempt limit and per-call/total timeouts. This change does not add another planning retry loop or change those settings. An interrupted planning start with no saved result remains review-required.

If a processing or worker budget expires while a planning operation has an uncertain outcome, the job's top-level error reports budget exhaustion and the operation is marked `OutcomeUnknown` with `provider_operation_outcome_unknown`. A late provider result cannot initiate a checkpoint after deadline cancellation. A storage commit already in flight may still finish; ownership fencing and the serialized checkpoint gate govern its result. Remote work already accepted cannot be cancelled retroactively.

These are per-job limits, not token, monetary, account-wide rate, or process-wide outage budgets. To deliberately request more work after reviewing a failed job, submit a new job with a new idempotency key. There is no in-place budget reset endpoint.

## Errors

- `job_retry_budget_exhausted`: no worker attempts remain.
- `normalization_retry_budget_exhausted`: no import attempts remain, or the requested retry delay exceeds the permitted maximum.
- `job_time_budget_exhausted`: the persisted processing deadline was reached.

All use the existing terminal `Failed` job status. Check operation status as well as the top-level error when deciding whether another planning call is appropriate. `Failed` describes planning-job processing, not an application test failure.

## Configuration

| Native setting under `Quality__Retry__` | Compose `.env` setting | Default |
| --- | --- | --- |
| `MaxWorkerAttempts` | `QUALITY_RETRY_MAX_WORKER_ATTEMPTS` | 5 |
| `MaxNormalizationAttempts` | `QUALITY_RETRY_MAX_NORMALIZATION_ATTEMPTS` | 3 |
| `MaxDurationSeconds` | `QUALITY_RETRY_MAX_DURATION_SECONDS` | 900 |
| `InitialDelayMilliseconds` | `QUALITY_RETRY_INITIAL_DELAY_MILLISECONDS` | 250 |
| `MaxDelayMilliseconds` | `QUALITY_RETRY_MAX_DELAY_MILLISECONDS` | 5000 |

For example, set `Quality__Retry__MaxWorkerAttempts=4` for native runs. Worker attempts must be 1–100; import attempts 1–10; duration 1–86400 seconds. Delays must be positive, initial must not exceed maximum, and maximum must not exceed 60000 milliseconds. Invalid settings fail startup.

Existing jobs retain their saved policy. Legacy jobs with no budget are assigned the fixed defaults atomically on their next claim; old historical attempts cannot be reconstructed. Use the same application version across worker roles; older binaries do not enforce these limits. No database migration is required because the fields live in the existing JSON document.

## Verification

Tests cover concurrent workers at the attempt limit, unchanged counters during lease renewal, policy preservation across replacement workers, legacy adoption, repeated interrupted imports, safe transient retries, permanent errors, long Retry-After values, cancellation during backoff, and a planning deadline with a provider that returns late. File-store and PostgreSQL checks exercise durable claim exhaustion and recovery.

The local `qwen/qwen3.8-27b` model supplied focused test-scenario suggestions. These were independently reviewed: its proposal to defer attempt counting until an outcome was rejected because a crash would bypass that count. The implementation commits usage at claim/invocation start.

Verified on 2026-09-16:

- 139 .NET tests passed with PostgreSQL checks isolated in temporary schemas. One unrelated MinIO integration test was skipped; the shared-database pipeline test was excluded to avoid altering the running stack's schema.
- Release and TypeScript builds passed; Release reported zero warnings/errors.
- All four browser/API tests passed, including the expanded job schema and budget fields.
- CLI replay/conflict verification passed. Custom-budget checks confirmed policy persistence across configuration changes and rejection of invalid settings before submission.
- Compose configuration and whitespace checks passed.

The application containers were not rebuilt or deployed. Local Qwen was used for review; no live requirements or cloud planning calls were required.

Explicit [job cancellation](job-cancellation.md) is terminal and revokes ownership. It stops retries and preserves saved results; worker shutdown remains a recoverable interruption.
