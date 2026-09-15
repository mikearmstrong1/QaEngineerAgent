# Reviewed Playwright execution

Step 3 adds a working Chromium execution adapter. Planning jobs still stop at `Completed`; execution is a separate, explicit CLI operation and produces its own durable `TestRun`. No test is run automatically after planning.

## Prepare, review and execute

Build the Release application and install the repository's pinned npm dependencies and Chromium as described in the README. Run these commands from the repository root:

```sh
# Replace JOB_ID with a completed planning job that contains tests.
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll prepare-execution \
  --job JOB_ID --target http://127.0.0.1:3000 > execution.json
```

The draft contains the plan ID, a hash of the persisted plan, test IDs and empty steps. Supply concrete selectors, actions and expected results from the application and requirement, then review the completed file. Empty drafts cannot execute. Tests can be a reviewed subset of the plan; a passing run only covers those selected IDs.

A test entry uses this structure:

```json
{
  "testCaseId": "TC-1",
  "steps": [
    { "action": "goto", "selector": null, "value": "/" },
    { "action": "expectText", "selector": "h1", "value": "Welcome" }
  ]
}
```

Supported actions are `goto`, `click`, `fill`, `expectText` (exact text), `expectVisible`, and `expectUrl` (exact resolved URL). Each test needs at least one assertion. Values are treated as data; manifests cannot provide scripts, imports, shell commands, custom fixtures or arbitrary evaluation. Source text and selectors are not inferred from a prose plan.

After review, record the exact file checksum and execute:

```sh
export Quality__Execution__AllowedOrigins='http://127.0.0.1:3000'
shasum -a 256 execution.json

dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll execute \
  --job JOB_ID --manifest execution.json --sha256 REVIEWED_SHA256

dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll get-run --id RUN_ID
```

`execute` verifies the supplied SHA-256 against the file bytes, validates the manifest schema and test mappings, and compares the plan hash with the saved plan. Any edit requires a new review/hash. The checksum binds execution to reviewed content; it is not an authenticated reviewer identity or signature. Demo/stub plans may be executed only through this same explicit manifest workflow.

## Configuration

| Variable | Default / meaning |
| --- | --- |
| `Quality__Execution__Workspace` | Current directory; must contain `playwright/execution`, schemas and installed npm dependencies |
| `Quality__Execution__RunDirectory` | `data/executions` under that workspace; durable local run records and artifacts |
| `Quality__Execution__AllowedOrigins` | Empty denies execution; semicolon-separated exact HTTP(S) origins, including ports |
| `Quality__Execution__NodeExecutable` | `node`; operator-controlled runtime path |

A standalone .NET publish requires an execution workspace with the Node assets and browser runtime. The Docker image already includes them. Compose forwards `QUALITY_EXECUTION_ALLOWED_ORIGINS` and mounts the `execution-data` volume at `/data/executions`. To use a host manifest with a one-shot container, mount it read-only and pass that container path to `--manifest`. Rebuild the image for these changes. Container target hostnames/origins must match the names visible inside the container.

## Execution and persistence

The adapter creates a fresh run directory, saves `Running`, snapshots the manifest, and generates a fixed Playwright spec/config from the reviewed data. The runner uses the installed Playwright CLI with Chromium, one worker, no retries, no inherited provider credentials or Node hooks, and fresh browser contexts. It does not install packages or invoke a shell.

Each directory retains:

- `run.json`: current/final TestRun with plan ID, manifest hash, selected test IDs, timestamps and executor version.
- `manifest.json`, `origins.json`, generated spec/config and reporter: execution inputs.
- `report.json`, `summary.json`, `result.json`, `process.log`: actual execution evidence.
- `artifacts/`: failure screenshots, traces and any Playwright error context.

Artifact keys are paths relative to the execution store, prefixed with the run ID. They identify local files; optional [MinIO publishing](minio-artifacts.md) adds remote metadata in `storedArtifacts` while preserving local keys. Run persistence uses atomic file replacement and is separate from the planning job's File/Postgres store. Shared deployments must share the run directory or mount the same volume to retrieve results.

`Passed` requires an actual successful Playwright report, exactly the selected tests, all tests passed, and a zero process exit. Failed assertions/test errors are `Failed`; `failureClassification` adds triage and explicit review without changing that status; see [regression promotion](regression-promotion.md). Global timeout is `TimedOut`; Ctrl-C is `Cancelled`; startup, missing/invalid reports and runner failures are `InfrastructureFailed`. Every terminal result gets a finish time. `execute` exits 0 only for `Passed`, 1 for nonpassing execution, and 2 for manifest/argument rejection. `get-run` returns 3 when the ID is absent.

A manifest sets a 1–300 second total browser-run timeout; each test is limited to at most 30 seconds. The .NET wrapper allows 15 seconds for process startup/report cleanup, then terminates the process tree. Cancellation also terminates the tree and saves the terminal state. A killed host or machine crash can leave `Running`; automatic recovery remains part of operational hardening.

## Target boundaries

The target and every explicit navigation/URL assertion must use an allowlisted origin. Browser HTTP requests are intercepted and fetched without following redirects; unlisted origins and all redirects are blocked. WebSockets and service workers are blocked. Network-policy failures prevent a passing test. Redirect-, WebSocket- or third-party-dependent application flows need an explicit later extension; do not treat their failures as application regressions.

Run directories and fresh processes provide working-file isolation, not an operating-system security sandbox. Use a dedicated execution environment and network controls when stronger isolation is required. The adapter does not execute arbitrary generated JavaScript. Review clicks/fills and use a test environment appropriate for their effects.

## Verification

```sh
dotnet test QualitySystem.sln -c Release
npm run test:execution
```

The execution suite starts temporary local fixture servers and drives real Chromium through the CLI. It tests passing/failing assertions, traces/screenshots, cross-process retrieval, schema validity, review/allowlist rejection, blocked redirects, timeout, cancellation, missing runtime, and zero-exit/missing-report handling. No external application is contacted.

Implementation references: [Playwright reporters](https://playwright.dev/docs/test-reporters), [timeouts](https://playwright.dev/docs/test-timeouts), [request routing](https://playwright.dev/docs/api/class-route).
