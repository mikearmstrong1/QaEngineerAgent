# Job contract v1

## HTTP

`POST /jobs`, `Content-Type: application/json`:

```json
{"reference":{"source":"jira","id":"AUTH-1427"}}
```

Required `reference.source`: `jira`, `coda`, or `stub`. `reference.id` must have 1–200 characters, contain a non-whitespace character, and no control characters. Jira IDs use `^[A-Z][A-Z0-9_]*-[1-9][0-9]*$`. Remote Coda ingestion expects `docId/tableId/rowId`. Additional request properties are rejected.

Returns `202 Accepted`, `Location: /jobs/<id>`, and the queued job snapshot. Each submission creates a new job; retried requests are not deduplicated yet. `400` reports invalid input. `GET /jobs/<id>` returns the latest job (`200`), `400` for malformed IDs, or `404` for missing IDs. IDs are 32 lowercase hexadecimal characters. `GET /health` reports liveness; `/ready` verifies job-store access and returns `503` if unavailable. API and worker both initialize persistence before starting.

A job includes `status`, `requirement`, `testPlan`, `decisions`, `transitions`, and `error`. Poll until `Completed` or `Failed`. `Completed` specifically means **plan produced**. Null requirement/plan indicates that checkpoint has not committed. Concurrency fields (`revision`, `leaseToken`, `leaseUntil`) are diagnostic and must not be modified by clients. There is no update-job endpoint.

All JSON uses camelCase property names, string enum values, and UTC ISO-8601 timestamps. See [job schema](../schemas/v1/job.schema.json), [request schema](../schemas/v1/job-request.schema.json), and [example](../schemas/examples/job-request.json).

## CLI

The same executable supports:

```text
Quality.Api api
Quality.Api worker
Quality.Api run --source jira --reference AUTH-1427
Quality.Api get --id <32-character-job-id>
Quality.Api --help
```

`run` submits to the configured store, attempts to claim that job, waits if another worker claimed it, and emits the completed/failed job as JSON. It calls the shared application service directly, not a remote HTTP endpoint. `get` reads the configured store. API, worker, run and get must use the same store settings to share jobs.

Exit codes: `0` success; `1` failed processing, timeout or infrastructure error; `2` invalid arguments/reference/configuration; `3` missing job. On one-shot timeout, inspect the persisted store; processing may finish later. Logs and error text go to stderr; a successful run/get emits only JSON to stdout.

## Configuration

| Environment variable | Default / role |
| --- | --- |
| `Quality__Store` | `File` natively, `Postgres` in image/Compose |
| `Quality__DataDirectory` | `./data/jobs`; file store only |
| `ConnectionStrings__Quality` | Required for Postgres; Npgsql connection string |
| `Quality__RunWorker` | `false`; optionally host worker inside API |
| `ASPNETCORE_URLS` | Set explicitly; examples use local port 5080, image uses 8080 |
| `QUALITY_BASE_URL` | Playwright only: test an existing API instead of spawning one |
| `QUALITY_ARTIFACT_DIR` | Playwright only: override report and trace output root (Compose uses `/tmp/quality-artifacts`) |
| `QUALITY_TEST_POSTGRES` | Tests only: enable PostgreSQL integration and mode verification |

Configuration comes from the standard .NET configuration providers. See [requirements ingestion](requirements-ingestion.md) and [structured planning](structured-planning.md) for provider configuration. Optional `planning` metadata on test plans and decisions records provider/model, version, prompt/schema hashes, attempts and response ID.

## Execution results

Planning status and execution status are separate. `prepare-execution`, `execute`, and `get-run` expose the reviewed Playwright workflow. TestRun adds optional `manifestHash` and `testCaseIds`; older records remain readable. See [execution contract](playwright-execution.md).

Optional `storedArtifacts` and `artifactUploadStatus` fields keep remote artifact metadata separate from execution status. FailureAnalysis can include `evidenceArtifacts`; association resolves evidence only from its own run. See [MinIO artifacts](minio-artifacts.md).

Optional `failureClassification` and `failureReviewReason` separate triage from execution status. Only explicit review labels an application failure. Promotion requires a still-passing run and exact reviewed manifest; see [regression promotion](regression-promotion.md).
