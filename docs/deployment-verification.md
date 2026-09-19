# Deployment verification — 2026-09-18

**Status checked 2026-09-19: verification, publication, and local Compose deployment passed.** The corrected verification branch and merged main passed their complete GitHub Actions workflows. The image from the clean verification commit passed the container, browser, persistence, integration, and HIGH/CRITICAL scan gates. Its published GHCR digest and platform match the local candidate, and a digest pull succeeded. See the [current build plan](next-steps.md).

## Verified candidate

See [machine-readable candidate record](deployment-candidate.json) for the exact local image digest, source commit, build-input fingerprint, scanner identity, test results, and evidence hashes. The candidate tag is `quality-system:verify-6a6f7ea` and its digest is:

```text
sha256:cabc4f2b72668b79950187cf598d774c1700a78d282a1058cb9a548250430971
```

This Linux arm64 candidate was built from clean commit `6a6f7ea5d255d7c04630f444811bcebc48d8530d`. It was subsequently published at immutable reference `ghcr.io/mikearmstrong1/qaengineeragent@sha256:cabc4f2b72668b79950187cf598d774c1700a78d282a1058cb9a548250430971`. The remote digest and platform matched, and an arm64 digest pull succeeded. No other architecture was verified in this run.

## Results

| Check | Result |
| --- | --- |
| September 18 source-clean Docker build | Passed for Linux arm64 from verification commit |
| Trivy 0.74.0, all HIGH/CRITICAL findings | Passed: 0 findings, no unfixed-vulnerability exclusions |
| .NET suite with isolated PostgreSQL test database and MinIO | 165 passed, 0 skipped |
| Reviewed browser execution and regression checks | 10 passed |
| Host Chromium/API suite against final container | 5 passed |
| Same suite inside the final read-only image | 5 passed |
| API/worker/CLI share one image; non-root and read-only runtime | Passed |
| Job and MinIO object durability after container recreation | Passed |
| Authenticated metrics, queued cancellation, repeated cancellation, submission replay | Passed |
| API and worker metrics authentication and scrapes | Passed |
| Cancellation stays terminal after worker start/recreation | Passed |
| MinIO adapter verification script on alternate endpoint | Passed |
| Remote GitHub Actions | Updated verification branch passed the complete workflow; image publication skipped |
| Registry publication / immutable registry deployment reference | Published; digest and arm64 manifest verified |
| Local Compose deployment | Passed: API and worker use the immutable digest; migrations, auth, persistence, MinIO, and five smoke tests passed |

Trivy used the official image pinned to `aquasec/trivy@sha256:62b1e65e8869bc4b4c6aa4fa2b21595256c7c2f6018a9d9ad61caf87187c1969` and scanned OS, Node, and .NET packages in the exact image digest above. The report timestamp and SHA-256 are in the candidate record. This result is limited to the selected severities and vulnerability database at scan time.

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

Testing used Compose project `quality-final-20260918`, its own network and named volumes, fresh test credentials, a separate PostgreSQL test database, and loopback ports 5088 (API), 5089 (worker metrics), 54339 (PostgreSQL), and 9010/9011 (MinIO). The original `quality-system` project on 5080/54329/9000/9001 remained running and was not recreated or migrated.

The verification scripts now support alternate endpoints:

- `scripts/verify-compose.py`: `QUALITY_VERIFY_API_URL` and `QUALITY_VERIFY_S3_HOST`, plus normal `COMPOSE_FILE` / `COMPOSE_PROJECT_NAME`; `--recreate` recreates only the selected project.
- `scripts/verify-artifacts.py`: `QUALITY_VERIFY_S3_URL` and the selected Compose configuration.
- `scripts/verify-container-controls.py`: requires an authenticated API with its worker stopped; uses `QUALITY_VERIFY_API_URL` and the selected Compose configuration. Start the worker after it passes.

CI generates a temporary API key, runs the authenticated container controls before starting the worker, and retains the HIGH/CRITICAL scan gate. The updated verification-branch run passed all these steps and skipped image publication. Local build, test, scan, and cleanup evidence is retained in `/tmp/quality-final-20260918`, with SHA-256 hashes in the candidate record. Temporary credentials were held in a mode-600 file outside the repository and removed after verification. The isolated containers, network, and synthetic test volumes were removed; the verified image remains locally available.

## Current checkpoint — 2026-09-18

The approved verification snapshot was pushed to `verify/deployment-20260916` at `264a940117617740868bf8530cce1f6ea14d5010`. [Run 35100842568](https://github.com/mikearmstrong1/QaEngineerAgent/actions/runs/35100842568) failed `RetryBudgetTests.RestartPreservesBackoffAndDoesNotSpendAnAttemptBeforeCalling` (expected Completed, actual Failed).

Local and remote main now match `7747fadbd68496ebf9efb65076c4b9f7ae580e20`, including the subsequent retry deadline fix. [Main run 35115693503](https://github.com/mikearmstrong1/QaEngineerAgent/actions/runs/35115693503) failed at the .NET test stage as well; its precise failure still needs investigation.

The focused Linux retry/timing checks passed 19 tests with one PostgreSQL skip. An earlier broader local run was aborted after 155 passes and one MinIO skip. On September 18, the frozen `JobTests.TestClock` was corrected to advance with elapsed time and an explicit atomic offset. The focused macOS job/retry suite passed 31 tests with one PostgreSQL skip, and the full Release .NET suite passed 165 tests with zero skips against disposable PostgreSQL and MinIO containers.

The verification branch was merged from current main and updated with the correction at `6a6f7ea5d255d7c04630f444811bcebc48d8530d`. [Run 35348519327](https://github.com/mikearmstrong1/QaEngineerAgent/actions/runs/35348519327) passed .NET, Node, browser, Compose, artifact, persistence/recreation, and HIGH/CRITICAL scan steps. Its image-publication step was skipped because the run was on the verification branch.

All prior changes were merged to main at `bbaa8b72ab7307c1e97518ce292bb8a138d1720c`. [Main run 35351744151](https://github.com/mikearmstrong1/QaEngineerAgent/actions/runs/35351744151) passed the complete workflow. The 96 files copied into the image have the same hashes as the locally verified arm64 candidate. Main CI no longer publishes the separate runner-built image.

The September 16 digest `sha256:8fc954c620c490f694f90047badb5072837bd7a400fe9cf9d95569ac2d9f7d81` is historical and was superseded by the September 18 candidate. The passing CI image was ephemeral; the exact verified arm64 candidate above is now published to GHCR by immutable digest.

## Remaining gates

Follow the [ordered build plan](next-steps.md): the deployment target is Linux arm64. The [published image](release-preparation.md) has an immutable GHCR digest matching the verified local candidate. The image is deployed to the local Linux arm64 Compose stack. The next planned gate is live-provider verification with configured credentials.
