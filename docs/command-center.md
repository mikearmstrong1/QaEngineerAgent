# Quality Command Center

The Command Center is an additional browser interface for requirement ingestion and plan review. It does not replace the API or any `Quality.Api` terminal command. CLI submission, retrieval, cancellation, execution, artifact publication, failure classification, and regression promotion retain their existing contracts.

Open [http://127.0.0.1:5081](http://127.0.0.1:5081) after starting Compose. The first release supports:

- API readiness and recent-job activity
- bounded, cursor-based job pagination
- Jira-key submission
- live polling through normalization and planning
- requirement, acceptance-criterion, test-case, assumption, coverage-gap, and planning-metadata review
- durable execution requests with manifest editing, server-side validation, optimistic revision checks, exact-hash approval, reviewer identity, and launch/status
- execution history for the selected plan, including status, automatic triage, evidence preview, and artifact download
- explicit human classification of failed runs with a required review reason
- regression patch creation, exact diff review, and explicit application to a configured test repository for eligible passing runs

The browser talks only to same-origin `/bff/*` routes. The Command Center service injects `QUALITY_API_KEY` when it calls the Quality API; the bearer key and Jira/OpenAI credentials are never placed in browser assets or responses. The Compose port binds to loopback. Deployments exposed beyond a trusted local machine need user authentication and TLS in front of the service.

Mutating BFF calls require the non-simple `X-Command-Center: 1` header and JSON content. Jira keys are normalized and validated again before forwarding. The Quality API remains authoritative for request validation and idempotency behavior.

## Review a failed run

1. Select the job whose reviewed manifest was executed.
2. Find the failed run under **Execution runs**.
3. Expand **Evidence**, then select **View** for safe inline text, JSON, and image previews or **Save** for any artifact type. The server reads the local execution copy first and can fall back to its associated MinIO or Azure Blob object.
4. Compare the evidence with the automatic triage recommendation.
5. Choose `ApplicationFailure`, `TestFailure`, or `InfrastructureFailure`, write the reason, and select **Save human review**.

The saved review is attached to the run. It does not change the execution result or promote regression coverage. The web form accepts only completed failing runs and uses the same `RegressionPromotion.ClassifyFailureAsync` service as the terminal command.

## Prepare and launch an execution

1. Select a completed planning job and enter an allowlisted target under **Reviewed execution**.
2. Select **Prepare manifest**. The saved draft contains every planned test with empty steps.
3. Add supported actions, selectors, values, and at least one assertion per selected test.
4. Select **Validate and save manifest**. Server validation binds it to the saved plan and advances its revision.
5. Review the complete JSON, target, and displayed SHA-256. Enter the reviewer identity, confirm the exact version, and approve it.
6. Select **Queue approved execution**. The durable worker claims the request, verifies the approved hash against the persisted bytes, starts Playwright, publishes remote artifacts when configured, and updates the displayed status.

Any saved edit clears the previous approval. Concurrent edits with a stale revision are rejected instead of overwriting a newer version. A worker interruption never replays potentially consequential browser actions: after the bounded lease expires, the request becomes `InfrastructureFailed` for review. TestRun metadata is shared through PostgreSQL while runner inputs and large evidence remain in the execution volume and optional object store.

## Autonomous execution

**Execution policies** appears below the recent-jobs list. It seeds `command-center-baseline` as a non-production, read-only/manual-launch policy for the local Command Center. Select an existing policy to view and edit it, or choose **New policy** to create one. Policy names are lowercase letters, digits, and hyphens; origins are absolute HTTP(S) origins without credentials; actions are selected explicitly. Policies are written atomically to the server-side execution volume and survive API restarts.

Automatic approval and launch are disabled until **Non-production only** is selected, and auto-launch also requires automatic approval. The API independently validates timeout and canary limits, origins, actions, and these relationships. The policy registry under **Reviewed execution** continues to show the exact fingerprint and policy enforced for an autonomous request. Select an eligible policy, enter a target covered by that policy, and choose **Start autonomous execution**.

The API then creates a durable request, performs its read-only page inspection, prepares a manifest, validates it against the policy, records the policy identity and fingerprint, and auto-approves it. A policy may auto-launch only its configured canary budget; later requests remain **Approved** and can be queued through the ordinary control. The Command Center can manage policies, but all execution authorization remains API-side and policies never include credentials.

## Terminal parity

Reviewed Playwright execution remains available through both the versioned `execution-*` commands and the original stateless CLI workflow. Failure classification and regression patch creation are available in both interfaces. The Command Center additionally previews and applies a reviewed patch to the configured target repository. Web controls invoke the same application services and preserve review hashes, promotion gates, and terminal commands.

## Promote a passing run

1. Configure `QUALITY_REGRESSION_REPOSITORY` with the absolute host path of the Git repository that owns the tests, then recreate the API, worker, and Command Center containers.
2. Select a job with a completed passing run.
3. Select **Create reviewable patch**.
4. Expand **Proposed files** and **Review exact Git patch**. Confirm the displayed target repository and SHA-256.
5. Check the review confirmation and select **Apply patch to target repository**.
6. Open the target repository, run its tests, and commit the added files through its normal review process.

The apply action runs Git's patch validation again and only permits new files under `playwright/regressions/REG-*/`. It rejects changed hashes, existing destinations, symbolic-link destinations, non-root repository configuration, and previously applied proposals. It does not commit, push, or open a pull request.

## Configuration

Compose supplies these settings to the server-side Command Center process:

| Setting | Compose value |
| --- | --- |
| `Quality__CommandCenter__ApiUrl` | `http://api:8080` |
| `Quality__CommandCenter__ApiKey` | `${QUALITY_API_KEY}` |

The service has its own `/health` endpoint. `/bff/status` verifies the upstream API store through `/ready`.

### Optional autonomous policy for local Compose

Configure one policy in your private environment file, then recreate API, worker, and Command Center:

```text
QUALITY_EXECUTION_ALLOWED_ORIGINS=http://host.docker.internal:5081
QUALITY_AUTONOMOUS_POLICY_NAME=command-center-smoke
QUALITY_AUTONOMOUS_POLICY_VERSION=v1
QUALITY_AUTONOMOUS_POLICY_ORIGIN=http://host.docker.internal:5081
QUALITY_AUTONOMOUS_POLICY_NON_PRODUCTION=true
QUALITY_AUTONOMOUS_POLICY_AUTO_APPROVE=true
QUALITY_AUTONOMOUS_POLICY_AUTO_LAUNCH=true
QUALITY_AUTONOMOUS_POLICY_CANARY_MAX_AUTO_LAUNCHES=1
```

The Compose default actions are `goto`, `expectText`, and `expectVisible`. Override the three `QUALITY_AUTONOMOUS_POLICY_ACTION_*` variables only for declared, non-production test flows. Do not put credentials or production targets in this policy.
