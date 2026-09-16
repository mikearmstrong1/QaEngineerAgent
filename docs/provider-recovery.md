# Provider-operation recovery

Every new normalization and planning operation records its start before calling the provider. The operation record and its result live inside the job document, so the file store and PostgreSQL save them atomically under the existing revision and lease checks. No new database migration is needed.

A successful call saves its result, completed operation record, and decision together, then advances the job stage in a separate save. A restarted worker can finish that transition without calling the provider again. Storage errors propagate to the worker rather than being mislabeled as provider failures.

## Recovery behavior

| Persisted state | What the next worker does |
| --- | --- |
| No operation start committed | Starts the operation normally |
| Requirement read started, no result | Repeats the read-only import within its saved retry budget, keeping the operation ID and incrementing attempts |
| Requirement or plan result committed | Reuses the result and advances the stage without another provider call |
| Planning started, no result committed | Marks the job Failed with `provider_operation_outcome_unknown`; does not call the planner again |
| Input differs from the recorded input hash | Marks the job Failed with `provider_operation_input_mismatch` |
| Operation state and result disagree | Marks the job Failed with `provider_operation_checkpoint_invalid` |
| Terminal job | Remains terminal; submissions using the same idempotency key return it unchanged |

A planning outcome is unknown when a process dies, is cancelled, or loses its lease after the start record commits and before its result commits. This includes the small window before the request actually leaves the process. The worker cannot distinguish that window from a request that completed remotely. It stops automatic replay in both cases.

The current planning adapter does not provide remote result recovery. This implementation does **not** guarantee exactly-once provider execution: retries within the adapter still follow its configured policy, and an accepted response lost before persistence cannot be reconstructed locally. The journal prevents a replacement worker from making a new planning call for that uncertain operation. Read-only requirement retries can observe a newer source revision; the first durably saved requirement becomes the input to planning.

## Reviewing an uncertain outcome

Read the job with `GET /jobs/<id>` or `get --id <id>`. Inspect `error`, `providerOperations`, decisions, and any saved requirement or plan. A failed job is not a failed application test and must not be treated as passing coverage.

For `provider_operation_outcome_unknown`, check provider-side records if available before deciding whether to request another plan. The locally generated operation ID is an audit identifier; it is not sent as a provider idempotency key, and a remote response ID might not have been saved. If another attempt is appropriate, submit a **new job with a new idempotency key**. The old job remains an audit record. There is no endpoint to overwrite or silently retry an uncertain operation.

For input/checkpoint errors, investigate the stored data and application version before requesting another plan. Do not edit concurrency fields to force a retry.

Retry and recovery attempts are bounded by [durable retry budgets](retry-budgets.md). Budget exhaustion preserves an uncertain planning operation as review-required.

## Contract

`providerOperations` is an optional array on jobs; older records can omit it. Each entry has:

- `id`, `stage` (`Normalize` or `Plan`), and `provider`.
- `inputHash`: SHA-256 of the serialized reference and normalization version, or the full persisted requirement and planning prompt version. It checks that a saved operation still belongs to its input; it is not a complete hash of provider configuration.
- `status`: `Started`, `Completed`, `Failed`, or `OutcomeUnknown`.
- `attempts`: application-level provider invocations begun, including safe import recovery attempts. It counts a committed start even if the process exits before sending a request. Planning metadata separately records HTTP attempts.
- `startedAt`, `finishedAt`, and a sanitized `error` code.

`Failed` records an exception reported by the provider adapter; it does not assert that no remote work happened. `OutcomeUnknown` records recovery that requires review. Job status remains the existing `Failed` value, so polling clients terminate normally. Optional `testPlan` data can exist while status is still `Planning`, or on a checkpoint-validation failure: consumers must check job status before treating the plan as completed.

Both stores reject a late save from an expired or superseded worker. Active workers now [renew ownership](lease-renewal.md). Recovery waits for the latest persisted lease to expire after a worker stops; the default lease duration remains five minutes.

## Upgrade and verification

Stop or drain old workers before upgrading. Legacy jobs without operation records remain readable and resume using their saved stage and outputs; calls begun by an older version cannot receive retrospective duplicate-call protection. Run the same application version across API, worker, and CLI roles.

`ProviderRecoveryTests` inject interruptions before and after start/result/terminal saves, using fresh service and store instances to recover. They cover safe import retries, saved-result reuse, unknown planning outcomes, changed inputs, cancellation, and late results from an old worker. PostgreSQL tests use temporary schemas and drop them afterward; they require `QUALITY_TEST_POSTGRES`. The file store retains its existing local-disk and power-loss durability limits.

Verified on 2026-09-15:

- Release build: zero warnings and errors; TypeScript build passed.
- .NET suite with isolated PostgreSQL checks: 105 passed, one unrelated MinIO integration test skipped. The older shared-database pipeline test was excluded to keep all PostgreSQL changes confined to temporary schemas.
- All ten interruption boundaries plus rejection of a late worker result passed against both file storage and real PostgreSQL.
- All four browser/API tests passed, including the expanded job schema and operation records.
- Isolated CLI submission replay/conflict verification passed.

The running Compose application image was not rebuilt or deployed. Live provider calls were not needed for these recovery checks.

Explicit [job cancellation](job-cancellation.md) is terminal and revokes ownership. It stops retries and preserves saved results; worker shutdown remains a recoverable interruption.
