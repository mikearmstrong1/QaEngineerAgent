# Reviewed regression promotion

Step 5 turns a passing reviewed execution into an isolated Git patch. It preserves the executed assertions and source mappings. It never changes a failing assertion to make a test pass, applies a patch automatically, pushes a branch, or opens a remote pull request.

## Propose coverage

Use a completed job with real requirements and a non-synthetic plan, plus a passing run of that exact plan. Review the saved manifest and retain its SHA-256:

```sh
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll promote-regression \
  --job JOB_ID --run RUN_ID --sha256 REVIEWED_MANIFEST_SHA256
```

The command checks the finished passing result, plan ID/hash, executed test IDs, requirement/criterion links and the exact saved manifest bytes. Synthetic plans, stale/tampered manifests, mismatched runs and failed runs are rejected. Reclassifying a failed run does not make it eligible for promotion.

The result names a `proposal.patch` with status `NeedsReview`. By default it lives in `data/proposals/RUN_ID/`, alongside:

- `proposal.json`: proposal branch label, review status, patch digest and file digests.
- `changes/`: an isolated Git repository with the new files staged on `regression/RUN_ID`.

This works even when the main workspace has no commits. The working repository is untouched. No author identity or remote account is needed. Existing destination files are rejected rather than updated; repeated proposals for the same run also fail rather than overwrite the prior review.

Each promoted test adds three files under `playwright/regressions/REG-<stable hash>/`:

- `manifest.json`: selected test with the exact executed actions/assertions, target and original test ID.
- `mapping.json`: stable ID, source requirement/revision, acceptance criteria/provenance, plan/run identifiers, review hash and evidence references.
- `test.spec.cjs`: a fixed wrapper using the repository's trusted shared action runner.

The stable ID is derived from requirement ID and original test-case ID, not run or plan IDs. It remains stable across repeat executions. If an upstream planner reassigns test IDs, identity must be reviewed; the adapter does not infer semantic equivalence or silently replace existing coverage.

## Review and apply

Review the proposed manifest, mappings and diff in the generated directory. The manifest can contain test data entered through `fill`; review its suitability for source control. Then, from the intended repository:

```sh
git apply --check /absolute/path/to/proposal.patch
git apply /absolute/path/to/proposal.patch
```

These commands are manual. Commit or submit a PR through your normal review workflow after inspecting the changes. The patch only adds regression files; it expects this version of the repository's shared runner, schema and regression configuration. It does not carry the entire application runtime.

Run promoted coverage explicitly:

```sh
QUALITY_REGRESSION_ORIGINS='http://127.0.0.1:3000' npm run test:regressions
```

The target is kept from the reviewed manifest. There is no implicit target rebasing; changing a target or assertion requires a new review. Origins must be explicitly allowed on each execution. The existing navigation, service-worker/WebSocket and redirect restrictions remain in force. The default smoke suite does not discover or run promoted regressions. Standalone regression execution writes Playwright reports under `data/`; it does not create a new .NET TestRun record.

## Failure classification

New execution records add `failureClassification` independently of their existing status:

| Classification | Meaning |
| --- | --- |
| `None` | Passing run |
| `NeedsReview` | Assertion mismatch or mixed failure signals; not yet a confirmed application defect |
| `TestFailure` | Failure while carrying out a reviewed test action |
| `InfrastructureFailure` | Runner/browser setup, total deadline, report or network-policy failure |
| `Cancelled` | Caller cancelled execution |
| `ApplicationFailure` | Explicit reviewer classification only |

Automatic classifications are triage signals. For example, an action timeout may come from a bad selector or an application problem. An assertion mismatch needs review against the requirement; it is not automatically labeled an application regression. Runtime annotations carry the classification so formatted error strings and source text do not determine it.

After checking the evidence, record a classification:

```sh
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll classify-failure \
  --run RUN_ID --classification ApplicationFailure \
  --reason 'Confirmed against the acceptance criterion and retained trace'
```

Allowed review categories are `ApplicationFailure`, `TestFailure` and `InfrastructureFailure`. The command saves the classification/reason on the run plus a timestamped local review file under `RUN_ID/reviews/`. It preserves the test outcome, manifest and assertions. Passing/running/cancelled runs cannot be classified as failures through this command. Reviews are recorded operator statements, not authenticated identities or signatures. They are local records and are not automatically uploaded by this slice.

## Configuration and boundaries

`Quality__SourceControl__Workspace` identifies the repository receiving the proposed paths (default: current directory). `Quality__SourceControl__ProposalDirectory` controls isolated proposal output (default: `data/proposals` in that workspace). Git must be installed on the host; the Dockerfile installs it at build time. Compose uses `/data/executions/proposals` on its persistent execution volume.

The patch adapter only permits new files in the regression namespace. It uses an isolated Git index with inherited Git overrides and hooks disabled. It generates a real staged Git diff without reading credentials or modifying the workspace's index. Branch pushing, remote PR creation, updates to existing regressions and automatic assertion healing are outside this implementation.

## Verification

```sh
dotnet test QualitySystem.sln -c Release
npm run test:execution
```

Tests cover promotion gates, stable IDs/mappings, exact assertion preservation, overwrite/traversal rejection, failure classification, real Git patch application, running the promoted regression in Chromium, and denying regression execution without an explicit origin allowlist.
