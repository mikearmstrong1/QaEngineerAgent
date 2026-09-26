# Project build plan

Last checked: **2026-09-26** against `main` after `0e37e6d`, the current source tree, local .NET/Node/Playwright verification, PostgreSQL, MinIO, and the documented deployment evidence.

## Product goals

The system should turn a Jira story into traceable, executable browser coverage; run that coverage safely; preserve evidence and regression history; and automate the normal path while making human review an optional policy gate or an escalation path. The web app and terminal must expose the same application contracts. Credentials must remain server-side and out of logs, browser responses, source control, and saved evidence.
The design should automate the entire process with optional human checkpoints and clear observability.

## Goal check

| Product goal | Current state | Remaining definition of done |
| --- | --- | --- |
| Consume Jira stories and build test cases | Implemented | Keep live-provider contract tests and surface source-revision drift before execution. |
| Create Playwright tests from test cases | Coverage-aware read-only preparation implemented | Expand semantic mappings and add governed test identity/data/preconditions/cleanup before mutations can run autonomously. |
| Execute tests and capture results | Implemented for reviewed and policy-approved requests | Per-test status, classification, duration, reports, logs, and failure traces/screenshots are persisted with each run. Make evidence publication resumable. |
| Automate safely with optional human review | Durable policy-bound workflow implemented | Add richer governance, per-step confidence, declared test data/cleanup, bounded reruns, and automated promotion proposals. |
| Persist a regression suite | Partially implemented | Replace patch-only promotion with a first-class immutable suite/version catalog and automated validation/PR creation. Keep merge approval external and explicit. |
| Demonstrate results traceable to Jira | Foundation implemented | Add a story-level acceptance criterion → test → manifest → run → evidence view and export. |

## Implemented baseline

- Read-only Jira and Coda ingestion, acceptance-criterion provenance, structured OpenAI planning, schema/traceability validation, bounded provider calls, and provider-operation recovery.
- Durable PostgreSQL job processing with migrations, leases, renewal, retries, cancellation, idempotent submission, authenticated APIs, metrics, and file-store development fallbacks.
- Hash-bound Playwright manifests, origin/action restrictions, isolated run directories, terminal run states, and local plus optional MinIO/Azure evidence.
- Reviewed regression proposals with stable mappings, Git patches, explicit apply, and failure classification.
- Command Center workflows for planning, manifest editing/approval, execution, evidence review, failure classification, regression proposals, and policy administration.
- A durable autonomous canary with immutable named non-production policy revisions, idempotent triggers, leased checkpoints, exact request snapshots, cancellation, optional review resumption, and atomic lifetime/rolling-window/concurrency launch budgets. Policies constrain origins, actions, timeout, approval, and launch. Read-only inspection can deterministically add heading/control assertions and narrowly matched `click`/`fill` actions; the API validates the resulting manifest before policy approval and launch.
- CI covers .NET, Node, Playwright, Compose, persistence/recreation, artifacts, browser smoke, and a HIGH/CRITICAL image scan. Historical release and deployment details remain in [deployment verification](deployment-verification.md) and [release preparation](release-preparation.md).

## Audit findings that change the old plan

1. **Autonomous mode is no longer the next unimplemented feature.** The canary exists on `main`; the next step is to make its authority, state, and recovery production-grade.
2. **Policy revisioning and launch budgets are implemented; richer governance remains.** Policy content is immutable by name/version, PostgreSQL and file stores enforce lifecycle state with one active revision per name, legacy file policies migrate, and requests retain the canonical policy snapshot/fingerprint and named environment. Lifetime, rolling-window, and concurrency budgets are reserved atomically before queueing. Test-identity/cleanup declarations, artifact requirements, and role-separated activation are still pending.
3. **Autonomous execution is now a durable workflow.** Trigger, planning validation, inspection, manifest preparation, policy evaluation, review, queueing, execution, evidence, and classification have persisted checkpoints, stage keys, bounded retries, fenced leases, cancellation, and resume behavior. Trigger idempotency converges duplicate deliveries and deterministic request identity prevents duplicate browser runs.
4. **Launch authorization and recovery are bounded.** Fingerprint-scoped lifetime, rolling-window, and concurrency limits reserve atomically. Policy gates, exhausted budgets, and fail-closed reservations pause the same workflow for review. Separately budgeted reruns remain future work.
5. **Generated coverage now fails closed and explains itself.** Manifest preparation persists every planned action and expected-result mapping, confidence, reason, structured gap, and mutation flag. Rotating assertions and the generic `body` fallback are removed. Incomplete, ambiguous, low-confidence, or mutating preparations pause for review; declared test identity/data/preconditions/cleanup remain pending.
6. **Terminal parity covers policy and workflow control.** CLI commands create/list/show/activate/disable/retire revisions and create/get/review/cancel automation workflows through the same services as the API and Command Center. Future governance fields must continue to ship across all three surfaces.
7. **The feedback loop remains human-heavy.** Failure classification, retry decisions, regression membership, source-control proposal validation, and story-level reporting are separate/manual operations rather than policy-driven stages.
8. **Operational scope remains local/private.** Bearer authentication is appropriate for the current deployment, but rate limiting, role separation, tenant isolation, and stronger execution sandboxing are still required before broader or multi-user exposure.

## Next work, in dependency order

### 1. Harden policy governance and restore CLI parity — in progress

Immutable server-side `ExecutionPolicyRevision` storage, canonical request snapshots, named environments, `Draft`/`Active`/`Disabled`/`Retired` lifecycle, one-active-revision enforcement, legacy-file migration, atomic lifetime/rolling-window/concurrency launch reservations, confidence thresholds, and CLI/API/web parity are implemented. Complete this milestone with artifact/retention requirements; test-identity references; cleanup requirements; richer escalation behavior; and role-separated activation. Store secrets only in the existing server-side configuration/provider boundary.

**Exit gate:** PostgreSQL concurrency tests prove atomic activation and budget reservation; an in-flight request remains bound to its saved policy revision after later edits; disable prevents new authorization without corrupting history; API, web, and CLI contract tests produce equivalent records; no policy response or evidence contains secret material.

### 2. Make autonomous operation a durable state machine — implemented

`AutomationWorkflow` now checkpoints `Triggered → Planned → Inspected → ManifestPrepared → PolicyEvaluated → AwaitingReview/Approved → Queued → Executed → EvidencePublished → Classified → Completed`. It persists input and stage idempotency keys, exact policy revision/hash, review decisions, request/run links, errors, escalation reasons, and an audit trail. File and PostgreSQL workers use lease/revision fencing and bounded retries. A review decision resumes the same aggregate; cancellation propagates into running execution. Regression proposal/catalog state remains in milestone 6 because policy does not yet authorize autonomous source-control changes.

**Exit gate:** restart/reconciliation tests prove one deterministic execution request and browser run; concurrent duplicate PostgreSQL triggers and claims converge; cancellation fences a running executor; bounded stage failures stop closed; uncertain or policy-gated work stops at `AwaitingReview`; every terminal workflow retains its checkpoints. Provider planning remains independently protected by the existing job operation ledger, uploads remain content-addressed/idempotent, and promotion remains an explicit later workflow.

### 3. Produce test-case-aware manifests with evidence of coverage — fail-closed foundation implemented

The read-only inventory now records role, accessible name, test ID, form relationship, link destination, uniqueness, disabled/read-only state, and capabilities. Every planned step and expected result receives a persisted mapping or structured gap with confidence and reasons. Generic assertions are forbidden; API, CLI, and Command Center expose read-only explanations. Policies set a minimum confidence, and incomplete/ambiguous/low-confidence coverage escalates. Mutation detection also escalates until the active policy can declare test identity/data, preconditions, and deterministic cleanup. Continue by expanding deterministic assertion/navigation mappings and adding those mutation-governance declarations.

**Exit gate:** golden fixtures cover ambiguous labels, duplicate controls, missing selectors, navigation, and destructive wording; low-confidence or incomplete mappings escalate; the autonomous path cannot approve a test with unmatched required steps, generic-only assertions, undeclared data, or uncertain cleanup; generated manifests remain deterministic for the same plan, policy, and inspection.

### 4. Add triggers and bounded reruns on top of the durable workflow

Support signed deployment/source webhooks and schedules that only create idempotent workflow triggers. Bind each trigger to an active policy revision and named environment. Add separately budgeted reruns for clearly identified infrastructure failures and declared flaky tests; reruns must reuse the exact manifest/policy and may never widen origins, selectors, actions, credentials, or data permissions.

**Exit gate:** signature/replay/expiry tests pass; duplicate delivery creates one workflow; invalid or stale triggers create no job; rerun exhaustion escalates once; schedules and webhooks can be disabled without deleting history; trigger submission is available from CLI for deterministic testing.

### 5. Automate deterministic triage and evidence publication

Automatically classify only evidence-backed infrastructure conditions and known test-harness failures. Leave assertion/application ambiguity in `AwaitingReview` unless a policy explicitly accepts a high-confidence rule. Make artifact publication resumable from abandoned `Uploading` states, apply retention policy by policy revision, and record redaction/checksum results before evidence is exposed or promoted.

**Exit gate:** classification fixtures have no false application-failure claims; uncertain results always escalate; interrupted uploads resume without rerunning tests; redaction tests cover headers, URLs, screenshots/text, and provider errors; missing required evidence prevents autonomous promotion.

### 6. Close the regression loop with immutable suite versions

Add durable `RegressionSuite`, `RegressionCase`, and immutable `RegressionVersion` records linking requirement/source revision, acceptance criteria, test case, policy revision, exact manifest, runs, evidence, confidence, and promotion state. Policy-eligible passing workflows may create membership proposals automatically. Materialize additive changes in an isolated worktree, run repository checks, and create a branch/commit/pull request. Keep merge approval and any target-repository permission broadening explicit.

**Exit gate:** repeated promotion is idempotent; existing coverage is never silently overwritten; rollback selects a prior immutable version; repository checks must pass before PR creation; failures leave a reviewable proposal and clean worktree; merge/deploy is never automatic.

### 7. Deliver story-level traceability and operational SLOs

Add an API, Command Center view, and export for Jira story/revision → criterion → planned test → manifest/version → run/rerun → evidence → regression membership. Add durable queue/workflow metrics, escalation counts, automation yield, false-escalation sampling, budget usage, and end-to-end latency. Define release gates and retention for the audit records.

**Exit gate:** every displayed status is derived from persisted lineage; exports verify hashes and identify gaps/stale source revisions; dashboards distinguish application, test, infrastructure, policy, and review waits; a release run proves the complete non-production story-to-PR path with human review disabled except for deliberately injected escalation cases.

## Default automation and review boundaries

| Stage | Automated by default when policy permits | Human review or escalation |
| --- | --- | --- |
| Ingest and plan | Read Jira, normalize, plan, validate, and deduplicate | Missing/changed criteria, uncertain provider result, or source-revision drift |
| Inspect and build manifest | Read-only semantic inspection and deterministic high-confidence mapping | Low confidence, unmatched required steps/assertions, undeclared data, or uncertain cleanup |
| Approve and run | Exact policy revision authorizes exact manifest; budget is reserved atomically | Production target, disabled policy, exceeded budget, unsupported action, or requested authority expansion |
| Triage and rerun | Known infrastructure/test-harness rules; exact-manifest bounded rerun | Ambiguous assertion failure, suspected product defect, exhausted retry budget, or conflicting evidence |
| Evidence and regression proposal | Private upload, checksums/redaction, immutable catalog proposal, repository checks, and PR creation | Missing evidence, overwrite/conflict, failed checks, policy exception, merge, deploy, or external configuration change |

Human review is therefore an explicit policy checkpoint and a safe fallback, not a mandatory step in the ordinary non-production path. Policy creation, activation, broadening, production access, merge, deployment, and external configuration changes remain operator actions.

## Verification checkpoint

- `dotnet test -c Release`: **209 passed, 1 skipped** against an isolated PostgreSQL database and MinIO resources. Only the credential-dependent Azure integration test was skipped.
- `npm run build`: **passed**.
- `QUALITY_SMOKE_PORT=5090 npm test`: **6 passed**.
- `npm run test:execution`: **12 passed**.

Use local Qwen for bounded implementation drafts, test proposals, and reviews where practical. Review its output and execute verification through the supervising agent; Qwen does not have independent shell access.
