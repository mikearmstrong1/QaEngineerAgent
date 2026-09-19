# Read-only requirements ingestion

The requirements adapter supports Jira Cloud issues and Coda table rows. Default mode remains `Stub` for repeatable demos. Set these environment variables on the worker or one-shot process (also on the API if it runs an embedded worker):

```sh
export Quality__Requirements__Mode=Remote
export Quality__Requirements__Jira__BaseUrl=https://your-team.atlassian.net
export Quality__Requirements__Jira__Email=reader@example.com
export Quality__Requirements__Jira__Token='<API token>'
export Quality__Requirements__Jira__AcceptanceField=customfield_10010
```

Submit the existing reference shape: `{"source":"jira","id":"AUTH-1427"}`. Configure the actual custom field ID for your site. Jira's issue ID and `updated` timestamp identify the source and revision. Plain text and Atlassian Document Format text fields are supported. Unsupported rich text nodes fail instead of silently discarding evidence.

For Coda:

```sh
export Quality__Requirements__Coda__Token='<API token>'
export Quality__Requirements__Coda__TitleColumn=c-title
export Quality__Requirements__Coda__DescriptionColumn=c-description
export Quality__Requirements__Coda__AcceptanceColumn=c-criteria
```

Use real column IDs and a reference such as `{"source":"coda","id":"docId/grid-tableId/i-rowId"}`. The document/table/row path and row `updatedAt` are retained in criterion provenance. This slice imports a single requirement row; it does not flatten entire documents or enumerate tables. Credentials only need read access and are supplied in headers. In Compose, pass the variables through to the relevant services using an override; `.env` alone does not automatically forward arbitrary variables.

Every explicit criterion records provider, resource ID, revision, field and locator. A text field is preserved as one criterion; array entries are separate criteria. IDs are deterministic for resource/field/array position, so reordered arrays can change criterion identity. The configured Jira custom field remains the preferred criteria source. If that field is absent, null, or empty, a top-level description paragraph or heading containing exactly `AC`, `AC:`, `Acceptance Criteria`, or `Acceptance Criteria:` starts an explicit criteria section; all following top-level description blocks form one criterion. Its provenance points to the first block after the marker. Other description prose is not inferred as criteria. Missing or empty criteria produce an empty collection and a risk entry. Actors and preconditions remain empty because this adapter does not infer them.

Only GET requests are issued, redirects are disabled in the application client, requests have a 30-second deadline, and responses are capped at 2 MiB. Authentication errors, missing source records, malformed data and rate limits fail the job with the existing sanitized `provider_failure`; there is no synthetic fallback or automatic retry. Explicit `stub` references remain available in Remote mode.

The default downstream planner is synthetic and marks its output `isStub: true`; [structured planning](structured-planning.md) can be enabled separately. Imported requirements and normalization decisions use `isStub: false`. Completed still means a plan was produced, not that application tests ran.

Connector tests use checked-in provider fixtures and a fake HTTP handler. Live account verification requires your configuration and has not been performed.

Provider contracts: [Jira Cloud issues API](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/), [Coda API v1](https://coda.io/developers/apis/v1).
