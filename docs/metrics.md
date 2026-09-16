# Operational metrics

The service exports a bounded, process-local snapshot in [Prometheus text format 0.0.4](https://prometheus.io/docs/instrumenting/exposition_formats/). Metrics measure job-store operations, provider invocations, and claimed worker attempts. No new packages, schema changes, or metrics database are required.

## Endpoints and configuration

The API exposes `GET /metrics` on its existing listener, with the same bearer-key access policy as `/jobs`. It returns `text/plain; version=0.0.4; charset=utf-8` and `Cache-Control: no-store`. Anonymous local-demo mode also applies to metrics. Scraping does not query persistence or increment counters.

Standalone workers keep their existing behavior with no HTTP listener by default. Set `Quality__Metrics__WorkerUrl=http://127.0.0.1:5081` to add a listener exposing only `/metrics`. Use the same `Quality__Api__Key` (or explicit local-demo anonymous setting) as the API. The URL must be one HTTP origin, without credentials, a path, query, or fragment. Invalid settings fail startup. A worker with metrics enabled still processes jobs independently of the API.

For Compose, set `QUALITY_METRICS_WORKER_URL=http://0.0.0.0:8081` and scrape `worker:8081/metrics` from the Compose network; no worker port is published to the host. Scrape the API at `api:8080/metrics`. Configure the collector with the bearer key for both targets. Each target must keep its own instance identity. An embedded worker reports into the API process's metrics.

```sh
curl -H "Authorization: Bearer $QUALITY_API_KEY" http://127.0.0.1:5080/metrics
```

Short-lived CLI processes collect metrics internally but do not expose a scrape listener. This is operational monitoring, not an audit ledger; saved jobs remain the authority for durable outcomes.

## Exported metrics

| Metric | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `quality_operations_total` | Counter | `operation`, `outcome` | Returned results or raised exceptions |
| `quality_operations_active` | Gauge | `operation` | Calls currently in progress |
| `quality_operation_duration_seconds` | Histogram | `operation`, plus `le` on buckets | Elapsed time measured with a monotonic clock |

All allowed series begin at zero. The fixed enum-backed label vocabulary bounds memory and output size, regardless of traffic or job count. Counts and buckets are rendered together under the same lock. Labels never include job IDs, references, provider names, keys, URLs, or exception messages. The store decorator preserves the underlying return values and exception types.

## Metric semantics

Metrics are process-local and reset upon restart; they do not represent durable job totals or queue depth.

`quality_operations_total(operation, outcome)` counts observed returns or exceptions for `submit`, `cancel`, `claim`, `save`, `renew`, `read`, `normalize`, `plan`, and `attempt`. Fixed outcomes include `success`, `replayed`, `conflict`, `missing`, `idle`, `completed`, `failed`, `cancelled`, `budget_exhausted`, `lease_lost`, `interrupted`, and `error`. Repeated cancels count every request. Idempotent submit replays record `replayed`. Conflicts are recorded even if the store returned the original job. Claims returning exhausted terminal jobs record `budget_exhausted` without starting an attempt. Failed provider attempts include retries, while OpenAI internal HTTP retries are not separate provider operations. A lost write acknowledgement is an `error` even if the write committed.

The active gauge measures executing calls. If a provider ignores cancellation, it remains active after the worker attempt stops. A late success never means the job completed.

Histograms measure duration in seconds, including errors, with cumulative buckets: `.005`, `.025`, `.1`, `.5`, `2`, `10`, `60`, `300`, and `+Inf`.

Scrape the API and worker separately. Labels do not contain IDs, keys, URLs, references, provider content, or exception strings. Render does not read the store. There are no HTTP traffic metrics, separate TestRun execution metrics, or percentiles calculated in-process.

Do not sum different operation categories to calculate job totals: one job produces several store operations. Idle queue polls appear as `claim/idle`; readiness checks and worker cancellation polling appear as `read` observations. Recovered planning results do not count as new provider calls. `attempt` begins only after a nonterminal job has been claimed, and includes its processing and lease cleanup. Transient import retries appear as additional `normalize` calls. Provider success measures the invocation result, before checkpoint persistence.

## Example queries

Completed worker-attempt rate:

```promql
sum(rate(quality_operations_total{operation="attempt",outcome="completed"}[5m]))
```

Average planning-call duration (seconds):

```promql
sum(rate(quality_operation_duration_seconds_sum{operation="plan"}[5m]))
/
sum(rate(quality_operation_duration_seconds_count{operation="plan"}[5m]))
```

Active worker attempts:

```promql
sum(quality_operations_active{operation="attempt"})
```

Durations include failures and interruption. A zero observation rate can produce an undefined average. Process restarts reset counters; use rates over scraped samples rather than subtracting arbitrary snapshots. These metrics do not aggregate durable queue depth, install a collector/dashboard, or change retry/cancellation policy.

## Verification

`JobMetricsTests` and `MetricsIntegrationTests` cover concurrent calls, active gauges, unchanged exceptions, secret exclusion, locale-independent cumulative histograms, idempotent submissions, cancellation, exhausted budgets, rejected leases, and detached providers. `scripts/verify-metrics.py` runs temporary API and worker processes and checks authenticated scrapes, duplicate-series rejection, histogram consistency, separate process counts, restart reset, and preserved jobs. The script is included in CI.

Verified locally on 2026-09-16: Release build passed with zero warnings/errors; the full .NET run passed 159 tests with one unrelated MinIO skip (the older shared-database test was excluded). All nine focused metrics tests passed after adding detached-provider cancellation and real PostgreSQL coverage. Browser, authenticated HTTP/CLI, and API/worker mode checks also passed. PostgreSQL verification uses temporary schemas. The running application stack was not rebuilt or deployed.
