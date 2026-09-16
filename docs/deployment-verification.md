# Deployment verification — 2026-09-16

**Local verification passed. Release verification is incomplete:** the user approved uploading a verification branch, and its remote CI run remains pending; registry publication and deployment have not happened.

## Verified candidate

See [machine-readable candidate record](deployment-candidate.json) for the exact local image digest, platform, build-input fingerprint, scanner identity, and results. The candidate tag is `quality-system:verify-20260916` and its digest is:

```text
sha256:8fc954c620c490f694f90047badb5072837bd7a400fe9cf9d95569ac2d9f7d81
```

This is a **local Linux arm64 candidate**, not a published registry-qualified deployment reference. Do not substitute the tag for an immutable registry deployment digest. No other architecture was verified in this run.

## Results

| Check | Result |
| --- | --- |
| Current-source Docker build | Passed after repairing npm installation |
| Trivy 0.74.0, all HIGH/CRITICAL findings | Passed: 0 findings, no unfixed-vulnerability exclusions |
| .NET suite with disposable PostgreSQL and MinIO | 163 passed, 0 skipped |
| Reviewed browser execution and regression checks | 10 passed |
| Host Chromium/API suite against final container | 5 passed |
| Same suite inside the final read-only image | 5 passed |
| API/worker/CLI share one image; non-root and read-only runtime | Passed |
| Job and MinIO object durability after container recreation | Passed |
| Authenticated metrics, queued cancellation, repeated cancellation, submission replay | Passed |
| Worker metrics authentication and process separation | Passed |
| Cancellation stays terminal after worker start/recreation | Passed |
| MinIO adapter verification script on alternate endpoint | Passed |
| Remote GitHub Actions for current changes | Pending verification-branch run; upload approved |
| Registry publication / immutable registry deployment reference | Not created |
| Deployment | Not performed |

Trivy used the official image pinned to `aquasec/trivy@sha256:62b1e65e8869bc4b4c6aa4fa2b21595256c7c2f6018a9d9ad61caf87187c1969`. The final report timestamp and SHA-256 are in the candidate record. This result is limited to the selected severities and vulnerability database at scan time; it does not claim absence of all vulnerabilities.

## Build repair

The initial local build reproduced the latest main-branch CI failure: installing dependencies inside npm's own project attempted to resolve unavailable `@npmcli/docs`. Removing that step allowed the build, but scanning found four HIGH findings in npm's bundled dependencies:

| Package | Old version | Verified replacement |
| --- | --- | --- |
| brace-expansion | 5.0.7 | 5.0.9 |
| ip-address | 10.2.0 | 10.3.1 |
| tar | 7.5.19 | 7.5.21 |

The Dockerfile now installs npm 12.0.2 and replaces those bundled copies using pinned public package tarballs fetched with scripts disabled. Registry dependency declarations were compared and remained compatible. The build's `npm ci`/TypeScript step and in-image browser suite verified npm still functions. The complete image was rescanned afterward; no scanner suppression was added. Recheck these temporary bundled-package replacements when updating npm.

Qwen supplied the verification checklist, repair proposals, container-control test draft, and evidence review. Its output was reviewed before execution: the draft's incorrect request shape/status/ID handling was corrected, unavailable in-image scan commands were omitted, and its suggestion to ignore unfixed vulnerabilities was rejected. Qwen did not have independent shell access; the supervising agent executed the reviewed steps.

## Isolation and repeatability

Testing used Compose project `quality-verify-20260916`, its own network and named volumes, fresh test credentials, and loopback ports 5088 (API), 5089 (worker metrics), 54339 (PostgreSQL), and 9010/9011 (MinIO). The original `quality-system` project on 5080/54329/9000/9001 was not recreated or migrated.

The verification scripts now support alternate endpoints:

- `scripts/verify-compose.py`: `QUALITY_VERIFY_API_URL` and `QUALITY_VERIFY_S3_HOST`, plus normal `COMPOSE_FILE` / `COMPOSE_PROJECT_NAME`; `--recreate` recreates only the selected project.
- `scripts/verify-artifacts.py`: `QUALITY_VERIFY_S3_URL` and the selected Compose configuration.
- `scripts/verify-container-controls.py`: requires an authenticated API with its worker stopped; uses `QUALITY_VERIFY_API_URL` and the selected Compose configuration. Start the worker after it passes.

CI now generates a temporary API key, runs the authenticated container controls before starting the worker, and retains the HIGH/CRITICAL scan gate. These workflow changes have not yet run remotely. Raw local logs, scanner reports, source-input hashes, and temporary Compose configuration are retained in `/tmp/quality-deployment`; temporary credentials are not committed.

## Remaining gates

Automatic approval review rejected creating and pushing a snapshot branch because the operation would export uncommitted source files and mutate a remote branch without explicit approval. That rejected attempt pushed no branch or commit. The user subsequently explicitly approved pushing a verification branch to the existing `mikearmstrong1/QaEngineerAgent` repository. Repository ownership was verified and the prepared snapshot passed a local secret scan. Main remains unchanged.

With upload approval granted, create a snapshot in a separate checkout, push only the verification branch, and run the complete workflow on that exact snapshot. Main-only image publication must remain disabled for that verification run. Resolve any platform/CI differences before a separately authorized registry publication. Record the registry-qualified digest of the published, verified artifact before deployment.
