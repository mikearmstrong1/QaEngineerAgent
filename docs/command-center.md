# Quality Command Center

The Command Center is an additional browser interface for requirement ingestion and plan review. It does not replace the API or any `Quality.Api` terminal command. CLI submission, retrieval, cancellation, execution, artifact publication, failure classification, and regression promotion retain their existing contracts.

Open [http://127.0.0.1:5081](http://127.0.0.1:5081) after starting Compose. The first release supports:

- API readiness and recent-job activity
- bounded, cursor-based job pagination
- Jira-key submission
- live polling through normalization and planning
- requirement, acceptance-criterion, test-case, assumption, coverage-gap, and planning-metadata review
- execution history for the selected plan, including status, automatic triage, and evidence references
- explicit human classification of failed runs with a required review reason
- regression patch creation, exact diff review, and explicit application to a configured test repository for eligible passing runs

The browser talks only to same-origin `/bff/*` routes. The Command Center service injects `QUALITY_API_KEY` when it calls the Quality API; the bearer key and Jira/OpenAI credentials are never placed in browser assets or responses. The Compose port binds to loopback. Deployments exposed beyond a trusted local machine need user authentication and TLS in front of the service.

Mutating BFF calls require the non-simple `X-Command-Center: 1` header and JSON content. Jira keys are normalized and validated again before forwarding. The Quality API remains authoritative for request validation and idempotency behavior.

## Review a failed run

1. Select the job whose reviewed manifest was executed.
2. Find the failed run under **Execution runs**.
3. Expand **Evidence references** and inspect the listed screenshots, traces, and logs in the execution data or configured artifact store.
4. Compare the evidence with the automatic triage recommendation.
5. Choose `ApplicationFailure`, `TestFailure`, or `InfrastructureFailure`, write the reason, and select **Save human review**.

The saved review is attached to the run. It does not change the execution result or promote regression coverage. The web form accepts only completed failing runs and uses the same `RegressionPromotion.ClassifyFailureAsync` service as the terminal command.

## Terminal parity

Reviewed Playwright execution remains available through the documented CLI workflow. Failure classification and regression patch creation are available in both interfaces. The Command Center additionally previews and applies a reviewed patch to the configured target repository. Web controls invoke the same application services and preserve review hashes, promotion gates, and terminal commands.

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
