# Engineering Quality System

Portable requirement-to-test planning with .NET, TypeScript, and Playwright. The service accepts a requirement reference, imports it through optional read-only Jira/Coda adapters (or creates a synthetic requirement in default Stub mode), creates a structured test plan through optional OpenAI planning (or a synthetic plan in default Stub mode), persists job transitions, and returns the result over HTTP or the CLI. `Completed` means planning completed; it does **not** mean application tests passed. Use [reviewed Playwright execution](docs/playwright-execution.md) to run tests and retrieve separate results, then [propose regression coverage](docs/regression-promotion.md) from passing runs.

## Quick start: native

Requirements: .NET SDK 10.0.401 (or a later 10.0.4xx patch), Node.js 20+, npm. The default file store requires no other services.

```sh
dotnet restore QualitySystem.sln --locked-mode
dotnet build QualitySystem.sln -c Release --no-restore
npm ci
npm run build
npx playwright install chromium

dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll run --source jira --reference AUTH-1427
```

The command prints one job JSON document to stdout and persists it in `data/jobs`. Logs/errors go to stderr. To inspect it from a new process:

```sh
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll get --id <job-id>
```

Run API and worker in **two terminals from the repository root**, sharing the default `data/jobs` directory:

```sh
# Terminal 1
Quality__Api__AllowAnonymous=true ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll api
# Terminal 2; worker has no HTTP listener
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll worker
```

For a single-process development API, set `Quality__RunWorker=true` on the API command. Open [local service](http://127.0.0.1:5080).

```sh
curl -i http://127.0.0.1:5080/jobs \
  -H 'Content-Type: application/json' \
  --data '{"reference":{"source":"jira","id":"AUTH-1427"}}'
curl http://127.0.0.1:5080/jobs/<job-id>
```

`POST /jobs` returns `202` and a `Location` header. Poll that URL for `Completed` or `Failed`. The default Stub mode does not contact external providers. Enable [real requirements ingestion](docs/requirements-ingestion.md) and [structured planning](docs/structured-planning.md) to import source content and produce validated plans.

## Quick start: containers

Requires Docker Engine/Desktop and Docker Compose v2 or newer.

```sh
cp .env.example .env
docker compose build api
docker compose up --no-build -d --wait
curl http://127.0.0.1:5080/ready
docker compose --profile cli run --rm oneshot run --source jira --reference AUTH-1427
docker compose --profile cli run --rm oneshot get --id <job-id>
docker compose logs worker
docker compose down
```

API, worker, and one-shot use the **same image**, changing only the command and environment. The image contains .NET runtime, Node, Playwright and browsers, schemas, prompts, and application code; startup performs no installation or source generation. Compose uses PostgreSQL by default, with named database and MinIO volumes. `docker compose down` preserves them; `down -v` deletes them.

MinIO uses the official Quay image (the former Docker Hub reference failed to pull). MinIO runs at [console](http://127.0.0.1:9001) / port 9000. Enable the [MinIO artifact adapter](docs/minio-artifacts.md) to upload execution evidence with checksums and retention. Initialize its dedicated bucket explicitly with `init-artifacts`; default Local mode makes no uploads. Development credentials are in `.env.example`. Ports bind to loopback. The example explicitly enables anonymous local development. For authenticated access, configure an API key as described in [API authentication](docs/api-authentication.md).

To use only container dependencies with native .NET:

```sh
docker compose up -d postgres minio
export Quality__Store=Postgres
export ConnectionStrings__Quality='Host=127.0.0.1;Port=54329;Database=quality;Username=quality;Password=quality-local-only'
# Run the API, worker or one-shot commands above with these variables.
```

## Verification

```sh
dotnet test QualitySystem.sln -c Release
npm run build
npm test
npm run test:execution
python3 scripts/verify-modes.py
```

Playwright starts a dedicated local API on port 5080 with the file store and an embedded worker; set `QUALITY_SMOKE_PORT` to a free port if another service is running there. Set `QUALITY_BASE_URL=http://127.0.0.1:5080` to test an already-running stack instead (it must have a worker). A Release .NET build is required before the default smoke test. On Linux install browser OS dependencies with `npx playwright install --with-deps chromium`.

The PostgreSQL integration test is explicitly skipped unless given a dedicated test database:

```sh
export QUALITY_TEST_POSTGRES='Host=127.0.0.1;Port=54329;Database=quality;Username=quality;Password=quality-local-only'
dotnet test QualitySystem.sln -c Release
python3 scripts/verify-modes.py
```

The mode verification script leaves synthetic jobs in PostgreSQL for inspection; it removes its temporary file-store jobs. See [verification results](docs/verification.md) for the checks actually run during scaffolding.

## Compose verification

After starting the stack:

```sh
python3 scripts/verify-compose.py
python3 scripts/verify-artifacts.py
# Optional: briefly recreate this project's containers and confirm persisted data survives.
python3 scripts/verify-compose.py --recreate
# Run Chromium, API, and contract smoke tests inside the same application image.
docker compose --profile test run --rm smoke
```

The Compose verifier checks API/worker planning, one-shot JSON and exit codes, shared image identity, the .NET 10 runtime, non-root/read-only settings, and a signed MinIO S3 upload/download. It creates synthetic jobs and a temporary test bucket, then deletes that bucket. With `--recreate`, PostgreSQL and MinIO named volumes must preserve the completed plan and test object across container replacement. The stack remains running after verification.

The `smoke` profile uses the same application image and writes browser output to `/tmp`, so its root filesystem remains read-only. Its temporary reports disappear with `--rm`; use the host Playwright command with `QUALITY_BASE_URL` to retain reports locally. Run `python3 scripts/verify-artifacts.py` to verify the application adapter and CLI against a temporary MinIO bucket.

On macOS, if Docker reports that `docker-credential-desktop` cannot be found, add `/Applications/Docker.app/Contents/Resources/bin` to your shell's PATH. The Compose verification script handles that location automatically.

## Repository

| Path | Purpose |
| --- | --- |
| `src/Quality.Domain` | Canonical models, reference validation, state transitions |
| `src/Quality.Orchestrator` | Job pipeline, adapter interfaces, explicit stubs |
| `src/Quality.Persistence` | PostgreSQL/file planning storage and local TestRun store |
| `src/Quality.Api` | Shared API / worker / one-shot / get entry point |
| `tests/Quality.Tests` | State, orchestration, concurrency, recovery and PostgreSQL tests |
| `playwright/sdk` | Typed HTTP client |
| `playwright/execution` | Trusted manifest-to-test runner and evidence reporter |
| `playwright/fixtures`, `seeds`, `page-objects`, `tests` | Reusable browser test structure and smoke tests |
| `schemas/v1`, `schemas/examples` | JSON Schema 2020-12 contracts and request example |
| `prompts/plan/v2` | Pinned system prompt and schema manifest for live planning |
| `prompts/normalize/v1`, `prompts/plan/v1` | Historical scaffold contracts |
| `scripts/verify-compose.py` | Container roles, runtime, storage and recreation checks |
| `scripts/verify-modes.py` | Cross-process API/worker/CLI and restart verification |
| `Dockerfile`, `docker-compose.yml` | One application image and local dependencies |

See [architecture](docs/architecture.md), [job contract](docs/job-contract.md), and [next steps](docs/next-steps.md). To work in this repository as a saved Codex project, add this folder in Codex; sidebar registration could not be automated in the originating task.
