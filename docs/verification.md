# Verification results

Verified on 2026-09-14 after the .NET 10 migration and Docker Desktop installation. Host: macOS arm64, .NET SDK 10.0.401 / runtime 10.0.12, Node 20.20.0. Container platform: Linux arm64, Docker Engine 29.8.0 / Docker Desktop 4.91.0 / Compose 5.5.1. Playwright 1.63.0; PostgreSQL 16.6 in Compose.

| Check | Result |
| --- | --- |
| .NET 10 Release build | PASS; 0 warnings, 0 errors |
| .NET tests, including Compose PostgreSQL integration | PASS; 13 passed, 0 failed, 0 skipped |
| Native API/worker/CLI and restart checks, file store | PASS |
| Native API/worker/CLI and restart checks, Compose PostgreSQL | PASS |
| TypeScript strict compilation | PASS, on host and during image build |
| Native .NET 10 API Playwright suite | PASS; 3 tests |
| Host Playwright suite against Compose API | PASS; 3 tests |
| Playwright suite inside the shared read-only application image | PASS; 3 tests, including a real Chromium browser navigation |
| Docker application image build | PASS; locked NuGet restore, .NET 10 publish, npm ci and TypeScript build |
| Compose configuration / startup | PASS; API, worker, PostgreSQL and MinIO running |
| API, PostgreSQL and MinIO readiness | PASS; all three health checks healthy |
| Container API/worker planning and one-shot/get JSON | PASS |
| Container CLI invalid input / missing job exit codes | PASS; 2 / 3 |
| Shared application image and execution settings | PASS; API/worker same image ID, all application modes same image reference, pwuser, read-only root filesystem |
| Container runtime | PASS; Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0.12 |
| PostgreSQL job durability after forced container recreation | PASS; same completed plan retrieved |
| MinIO signed S3 upload / download | PASS |
| MinIO object durability after forced container recreation | PASS; probe object retrieved, then temporary bucket/object removed |
| npm installation audit | 0 known vulnerabilities reported during image build |
| CI YAML | Parsed; workflow updated to repeat container verification and smoke suite |
| Remote GitHub Actions execution | NOT RUN |
| Other container architectures | NOT TESTED; verified Linux arm64 on this host |
| Codex saved-project sidebar registration | Original limitation unchanged; add repository folder manually |

Verified local application image ID:

```text
sha256:4cc608b380c1ceaf750b6d4dca5136f1adf876aa7e58015eff0f3152816c2573
```

This is a local build identity, not a published registry deployment reference. The image has not been pushed to a registry. Build once and publish/promote its resulting digest when deployment is added.

## Changes made for the migration

- Targeted all projects at `net10.0`, pinned SDK 10.0.401 and container runtime 10.0.12, regenerated NuGet lockfiles, and updated native executable paths.
- Installed the SHA-512-verified official macOS .NET 10 SDK alongside the existing SDK.
- Replaced the failing Docker Hub MinIO reference with the same release on its official Quay repository and added a readiness check.
- Added a Compose smoke profile that reuses the application image, with browser reports and npm cache directed to writable temporary storage.
- Added `scripts/verify-compose.py` for repeatable role, runtime, S3 and persistence checks. `--recreate` briefly replaces project containers without deleting named volumes.
- Updated README and CI to build the application image once, start the stack, verify container recreation, and run browser smoke tests inside the image and from the host.

The 13 .NET cases include 12 local unit/store cases and one PostgreSQL integration case. They cover valid/invalid references, ordered transitions, sanitized provider failure, exclusive claims, stale lease fencing, checkpoint resume, cancellation recovery, and unconfigured executor behavior. The failed-job CLI exit path (1) remains implemented but was not injected end-to-end through the CLI.

The application still uses stub requirement and LLM providers. The MinIO check verifies the storage dependency; the application's artifact adapter remains a stub. `Completed` means a structured plan was produced, not that generated application tests executed. The Playwright browser smoke navigates this service's status page.

The Compose stack was left running on loopback ports 5080 (API), 54329 (PostgreSQL), 9000 (MinIO S3), and 9001 (MinIO console). Stop it with `docker compose down` from the repository; named volumes remain. Synthetic verification jobs are retained for inspection.

## Requirements ingestion slice — 2026-09-14

- Release solution build passed with zero warnings/errors.
- .NET tests: 25 passed, 1 PostgreSQL integration test skipped (no dedicated database configured). Includes fixture-based Jira/Coda imports, deterministic criterion IDs, provenance persistence and audit flags, missing criteria, authorization/rate-limit/redirect failures, malformed responses, cancellation, unsafe configuration and response-size limits.
- TypeScript build passed.
- Strict JSON Schema validation passed for the existing completed-job example and the same contract extended with criterion provenance.
- The test runner required execution outside the filesystem sandbox to open its local communication socket.
- Live Jira/Coda accounts, Docker rebuild, and browser smoke tests were not exercised for this slice.

## Structured planning slice — 2026-09-14

- Release solution build and standalone publish passed.
- .NET tests: 53 passed; 1 PostgreSQL integration test skipped because a dedicated database was not configured. Planning tests use fake HTTP responses; no live OpenAI calls were made.
- TypeScript build passed. All 3 Chromium/HTTP/schema smoke tests passed on port 59314; port 5080 was occupied, so the existing service was left running.
- Published CLI, run from outside the repository, successfully loaded bundled prompt assets and returned a no-call gap plan for synthetic input in OpenAI mode. Its complete job passed strict canonical JSON Schema validation, and another process retrieved the same persisted planning hash.
- The sandboxed CLI verification stalled and was stopped; the same check passed outside the sandbox. Test runner and browser verification also used local execution outside the sandbox.
- Live API access/model quality, Docker image rebuild and PostgreSQL integration were not verified for this slice.


## Playwright execution slice — 2026-09-14

- Release .NET build passed without warnings/errors; 69 .NET tests passed, with 1 PostgreSQL integration test skipped because no dedicated database was configured.
- All 8 real Chromium execution integration checks passed against temporary local fixture servers: pass/report retrieval/schema, failed assertion with trace/screenshot, checksum/origin rejection, redirect blocking with zero requests to the disallowed server, timeout, missing runtime, cancellation, and zero-exit/missing-report rejection.
- TypeScript build and all 3 existing browser/API planning smoke tests passed (port 59314).
- Added execution checks to the CI workflow. Remote CI and rebuilt Docker/Compose execution were not run in this slice.
- Integration checks used temporary stores and fixture pages; no external application was tested. Local socket/browser tests required execution outside the filesystem sandbox.

## MinIO artifact slice — 2026-09-14

- Release .NET build passed with zero warnings/errors. Standard locked restore and package audit passed.
- Default .NET suite: 77 passed, 2 skipped (PostgreSQL and opt-in MinIO integration).
- Real MinIO integration then executed separately and passed: CLI bucket initialization/publishing, retention-rule configuration, checksum/content-type metadata, verified download, repeated publishing and failure-evidence association. Its unique test bucket and local test data were removed.
- All 8 Chromium execution checks and all 3 browser/API smoke checks passed; TypeScript build passed.
- CI includes the MinIO adapter verifier. The Docker image and remote CI were not rebuilt/run for this slice; testing used the native application against the existing local MinIO dependency.
- Retention was verified by reading the configured lifecycle rule; actual day-based expiration was not awaited. Tests used fixture evidence, not user application data. Local sockets/Docker access required execution outside the filesystem sandbox.

## Regression promotion slice — 2026-09-14

- Release .NET build passed with zero warnings/errors. 86 .NET tests passed; PostgreSQL and MinIO integration tests were skipped in this slice.
- Nine execution/promotion integration tests passed together. The added malformed-locator classification test then passed separately against the same implementation (10 distinct execution/promotion checks verified).
- The end-to-end promotion check ran a reviewed fixture in Chromium, produced a real staged Git patch, applied it in a temporary repository, ran the promoted regression successfully, and verified denial without an origin allowlist.
- Verified assertion mismatches remain NeedsReview until explicit classification; test locator failures and infrastructure/network-policy failures are distinguished. No assertion was weakened or source test overwritten.
- TypeScript build and all three browser/API planning smoke checks passed.
- The Dockerfile now includes Git and Compose persists proposals, but the image was not rebuilt in this slice. No patch was applied to the user's working repository, and no remote branch or PR was created. Local tests used temporary repositories and fixture servers outside the filesystem sandbox.

## Deployment verification — 2026-09-16

See [current candidate results](deployment-verification.md) and [image identity](deployment-candidate.json). Local current-source container verification passed, including a clean HIGH/CRITICAL scan after npm repair. Remote CI, registry publication and deployment remain pending.
