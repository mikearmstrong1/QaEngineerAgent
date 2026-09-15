# Structured planning provider

Step 2 adds an optional OpenAI Responses API provider. The default planner remains `Stub` for offline demos. Live planning submits the normalized requirement to OpenAI and returns a proposed plan for review; it never executes tests.

## Enable on a native worker or one-shot process

```sh
export Quality__Planning__Mode=OpenAI
export Quality__Planning__Model='<your supported model or snapshot ID>'
export OPENAI_API_KEY='<your API key>'
```

Choose a model that supports the Responses API and strict JSON Schema outputs. No model is chosen automatically; use a snapshot ID when reproducibility matters. The configured ID and the returned model ID are both persisted. `Quality__Planning__ApiKey` may be used instead of `OPENAI_API_KEY`.

Combine this with [Remote requirements configuration](requirements-ingestion.md), then submit an ordinary requirement reference. Setting planning mode alone leaves requirement ingestion in Stub mode, which intentionally produces a no-call gap report instead of sending synthetic requirements to the model.

For Compose, `.env` supports `QUALITY_PLANNING_MODE=OpenAI`, `QUALITY_PLANNING_MODEL`, and `OPENAI_API_KEY`; the shared service environment forwards these values. Supply the requirement-adapter variables through a Compose override as described in the ingestion guide. Rebuild the image for this implementation before enabling it.

## Prompt and validation contract

`prompts/plan/v2/manifest.json` pins the SHA-256 digests of `system.md` and `schemas/v1/planning-output.schema.json`. The application loads them once at startup and rejects missing files, unexpected manifest paths/version, or changed contents. Build and publish outputs include these files. `Quality__Planning__AssetDirectory` can override their root; it defaults to the application's binary directory. The previous `plan/v1` contract remains unchanged for historical reference.

The request uses `text.format` with `type=json_schema` and `strict=true`, supplies the pinned system prompt as instructions, and serializes the requirement as user data. No tools are configured. Provider-side response storage is disabled with `store=false`.

The returned JSON is validated locally with JsonSchema.Net against the same schema. Additional properties, missing/null fields, invalid categories/priorities, empty steps and duplicate JSON keys are rejected. Every test must link to known criterion IDs; duplicate test IDs or repeated criterion links are rejected. The application supplies plan and requirement IDs, `automationStatus=Planned`, prompt version, stub flags and provider metadata.

Missing, blank or duplicate acceptance criteria, or synthetic input, produce an empty plan with explicit coverage gaps and **zero API attempts**. Empty model-generated plans are also valid. The application adds gaps for criteria without proposed coverage, missing title/description/actors/preconditions, and source risks. No missing source fields are filled with invented values. A valid schema and traceability links do not establish that every proposed action is semantically correct: review the plan, assumptions and gaps before use.

## Bounds and failure behavior

| Setting under `Quality__Planning__` | Default | Allowed |
| --- | --- | --- |
| `MaxAttempts` | 3 | 1–3 |
| `AttemptTimeoutSeconds` | 20 | 1–60, at most total timeout |
| `TotalTimeoutSeconds` | 75 | 1–80 |
| `MaxOutputTokens` | 8192 | 256–16384 |

The total timeout covers all requests and retry delays. These limits fit inside the existing five-minute job lease and, with normal ingestion, the two-minute one-shot deadline. Inputs are capped at 1 MiB and responses at 2 MiB. Requests use the fixed HTTPS OpenAI endpoint, with redirects disabled.

HTTP 408, 429 and 5xx responses, transport failures and per-attempt timeouts may retry within the budget. Retry delays start at 250 ms and increase exponentially. `Retry-After` is honored; a delay beyond the budget ends the attempt rather than retrying early. Retried requests may incur additional API usage. Invalid output, refusal, incomplete output and permanent HTTP failures do not retry or fall back to synthetic content.

Failures persist a sanitized code such as `planning_http_error`, `planning_invalid_output`, `planning_invalid_traceability`, `planning_refused`, `planning_incomplete`, `planning_transport_error`, `planning_retry_budget_exhausted` or `planning_timeout`. Raw provider errors and refusal text are not stored. Caller cancellation preserves the existing recoverable job behavior.

`testPlan.planning` and the planning decision record provider, requested/returned model, adapter/API version, prompt/schema hashes, attempt count and response ID. Failed planning attempts also retain this metadata where available. A no-call gap plan has attempts 0 and null returned model/response ID. These fields are optional additions to the existing v1 storage contracts, so older jobs remain readable. `Completed` means planning finished, including an empty gap report; it does not mean tests passed or coverage is sufficient.

## Verification and remaining limits

Fixtures exercise provider responses without API credentials or live API calls. Tests cover schema and traceability rejection, missing requirements, retries, cancellation/deadlines, refusal/incomplete output, prompt integrity, response size, and file-store persistence. Live model quality and account access must be checked separately with configured credentials. Automatic request deduplication across worker crashes remains part of operational hardening.

Contracts used: [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [Responses API](https://developers.openai.com/api/reference/cli/resources/responses/methods/create), [JsonSchema.Net](https://docs.json-everything.net/schema/basics/).
