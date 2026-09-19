# Live provider verification

## Jira Cloud — passed 2026-09-19

The published Linux arm64 image was run in one-shot mode against the `KAN-2` Story in the team-managed `KAN` Jira Cloud project. Requirements mode was `Remote`; planning remained `Stub` so this check isolated the requirements adapter.

- Jira authentication with the configured account email and API token succeeded.
- Jira returned HTTP 200 for the issue request.
- The job `1a78d04d3cf64e4d8b724be4c38192d2` completed without an error.
- The normalized requirement is non-stub and contains one explicit acceptance criterion.
- Criterion provenance records provider `jira`, Jira resource ID `10011`, Jira's updated revision, field `customfield_10045`, and locator `/customfield_10045`.
- The criterion originated from the project Story field `Acceptance Criteria`, populated using Atlassian Document Format.

The credential remains only in the ignored, mode-600 `.env` file. It is not recorded in source or evidence. Rotate the token after verification because it was supplied through chat.

## Outstanding live checks

- Coda ingestion requires a Coda API token, real document/table/row reference, and title, description, and acceptance-column IDs.
- OpenAI planning requires an API key and a model ID that supports the Responses API and strict JSON Schema output. It should be exercised with the imported non-stub Jira requirement so the provider is actually called.
