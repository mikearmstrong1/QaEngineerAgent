# Live provider verification

## Jira Cloud — passed 2026-09-19

The published Linux arm64 image was run in one-shot mode against the `KAN-2` Story in the team-managed `KAN` Jira Cloud project. Requirements mode was `Remote`; planning remained `Stub` so this check isolated the requirements adapter.

- Jira authentication with the configured account email and API token succeeded.
- Jira returned HTTP 200 for the issue request.
- The job `1a78d04d3cf64e4d8b724be4c38192d2` completed without an error.
- The normalized requirement is non-stub and contains one explicit acceptance criterion.
- Criterion provenance records provider `jira`, Jira resource ID `10011`, Jira's updated revision, field `customfield_10045`, and locator `/customfield_10045`.
- The criterion originated from the project Story field `Acceptance Criteria`, populated using Atlassian Document Format.

### Description-embedded criteria

The Jira card layout used by `KAN-4` stores its criteria inside the main description rather than a separate field. The adapter now recognizes an explicit top-level `AC`, `AC:`, `Acceptance Criteria`, or `Acceptance Criteria:` marker when the configured custom field is absent or empty. It keeps all following top-level ADF blocks together as one criterion while retaining the full card description separately.

Live verification against `KAN-4` completed without error using temporary local job storage. It imported one non-stub, 4,074-character criterion containing the Range Builder, Chart Behavior, support, drilldown, KBo, dashboard-email, and Smart Banners sections. Provenance records Jira resource `10013`, the live updated revision, field `description`, and locator `/description/content/4`.

The final Linux arm64 release was built from commit `640c6cf4c6ac316f527f9d0b3d64ce76c46c23e2`, scanned with zero HIGH/CRITICAL findings, published, and deployed as:

```text
ghcr.io/mikearmstrong1/qaengineeragent@sha256:bca9a494209884521698d13c58dfc7561f00dd531455902ef3da1e47fe12b501
```

The deployed API job `3654630326a6481d8d01825ffa56e236` completed the same live `KAN-4` import with the full 4,074-character criterion and `/description/content/4` provenance. API and worker container identities match the immutable digest, API health passed, all five built-in browser/API smoke tests passed in Remote requirements mode, and no startup errors were present. A pre-deployment PostgreSQL backup was saved at `/private/tmp/quality-pre-jira-ac-20260919.dump` with SHA-256 `98e9785baf8ab4bec4baf19a61a7edd6d819ee3604acb90caf4ae299867b6553`.

The credential remains only in the ignored, mode-600 `.env` file. It is not recorded in source or evidence.

## OpenAI planning — passed 2026-09-19

The deployed worker planned the live, non-stub `KAN-4` Jira requirement through the OpenAI Responses API using `gpt-5.6-terra`. Job `7f45df6514c64c279bd81961580c252a` completed in one provider attempt and produced a non-stub plan with 15 test cases, four assumptions, and eight explicit coverage gaps.

Local validation accepted the strict planning schema. All test IDs were unique, every criterion link resolved to the imported Jira criterion, and the persisted planning metadata included the requested and returned model IDs, response ID, prompt hash, and schema hash. The provider operation completed without an application error, and the API and worker remained healthy.

The default three 20-second attempts were too short for the first live call and correctly ended with the sanitized `planning_timeout` error. Compose now forwards the existing attempt count, attempt timeout, total timeout, and maximum output token settings. The successful verification used one 60-second attempt inside the 75-second total budget.

## Outstanding live checks

- Coda ingestion requires a Coda API token, real document/table/row reference, and title, description, and acceptance-column IDs.
