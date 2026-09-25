# Engineering Quality System

Turn a Jira story into a structured, reviewable test plan. The system imports the story, sends its acceptance criteria to the configured planner, saves the result, and shows it in a local web app.

> **Completed means the test plan was generated. It does not mean the application was tested.** Running browser tests is a separate, reviewed step.

## The easiest way to use it

You need:

- Docker Desktop, with Docker Compose v2
- A Jira Cloud account and API token
- An OpenAI API key
- This repository checked out locally

Run every command below from the repository root.

### 1. Create your local settings file

```sh
cp .env.example .env
chmod 600 .env
```

Open `.env` and set these values:

```dotenv
# Protect the local API. Use a long random value.
QUALITY_API_ALLOW_ANONYMOUS=false
QUALITY_API_KEY=YOUR_RANDOM_LOCAL_API_KEY

# Import real Jira stories.
QUALITY_REQUIREMENTS_MODE=Remote
JIRA_BASE_URL=https://YOUR_SITE.atlassian.net
JIRA_EMAIL=YOUR_JIRA_EMAIL
JIRA_TOKEN=YOUR_JIRA_API_TOKEN
JIRA_ACCEPTANCE_FIELD=YOUR_OPTIONAL_CUSTOM_FIELD_ID

# Generate plans with OpenAI.
QUALITY_PLANNING_MODE=OpenAI
QUALITY_PLANNING_MODEL=gpt-5.6-terra
OPENAI_API_KEY=YOUR_OPENAI_API_KEY
QUALITY_PLANNING_MAX_ATTEMPTS=1
QUALITY_PLANNING_ATTEMPT_TIMEOUT_SECONDS=60
QUALITY_PLANNING_TOTAL_TIMEOUT_SECONDS=75

# Absolute path to the Git repository that owns the regression tests.
QUALITY_REGRESSION_REPOSITORY=/absolute/path/to/your-test-repository
```

`JIRA_ACCEPTANCE_FIELD` is optional when the Jira description contains a top-level `AC` or `Acceptance Criteria` heading. Keep `.env` private; Git ignores it.

### 2. Build and start everything

```sh
docker compose build api
docker compose up --no-build -d --wait
```

The first build can take several minutes. A successful start reports healthy containers.

### 3. Open the Command Center

Open [http://127.0.0.1:5081](http://127.0.0.1:5081).

You should see **Services ready** in the upper-right corner.

### 4. Generate a plan

1. Enter a Jira key, such as `KAN-4`.
2. Select **Generate plan**.
3. Wait while the job moves through `Queued`, `Normalizing`, and `Planning`.
4. Select the completed job from **Recent jobs**.

### 5. Review the result

Check each section before using the plan:

- **Acceptance criteria:** Confirm the correct Jira content was imported.
- **Proposed tests:** Review priorities, categories, steps, and expected results.
- **Assumptions:** Confirm the planner did not rely on a false assumption.
- **Coverage gaps:** Decide whether the Jira story needs more detail.
- **Model metadata:** Confirm the plan came from the expected provider and model.

The Command Center creates and reviews plans, persists versioned execution manifests, records exact-hash human or policy approval, launches only the approved bytes, shows runs and evidence, and lets a person classify failures. A named non-production policy may auto-approve and canary-launch a conforming generated manifest; policy creation or broadening remains an operator action. It does not run an unapproved manifest or change source code automatically. See [Command Center autonomous execution](docs/command-center.md#autonomous-execution).

## Everyday commands

### Check service status

```sh
docker compose ps
curl http://127.0.0.1:5080/ready
```

### Watch the worker

```sh
docker compose logs -f worker
```

Press `Ctrl+C` to stop watching. The services keep running.

### Restart after editing `.env`

```sh
docker compose up --no-build --force-recreate -d --wait api worker command-center
```

### Stop the system

```sh
docker compose down
```

This preserves PostgreSQL, MinIO, and execution volumes. `docker compose down -v` permanently deletes those volumes.

## Use the terminal instead of the web app

The web app is optional. Every workflow remains available from the terminal.

### Submit a Jira story and wait for its plan

```sh
docker compose --profile cli run --rm oneshot \
  run --source jira --reference KAN-4
```

The command prints the completed job as JSON. Copy its 32-character `id` value.

### Get a saved job

```sh
docker compose --profile cli run --rm oneshot \
  get --id JOB_ID
```

### Cancel an active job

```sh
docker compose --profile cli run --rm oneshot \
  cancel --id JOB_ID
```

### List jobs through the HTTP API

The Command Center uses the authenticated API on port 5080. `GET /jobs` returns newest jobs first:

```sh
curl 'http://127.0.0.1:5080/jobs?limit=20' \
  -H "Authorization: Bearer YOUR_RANDOM_LOCAL_API_KEY"
```

Pass the returned `nextCursor` as `?limit=20&cursor=NEXT_CURSOR` to load older jobs.

### Manage autonomous policy revisions

Policy content is immutable by name and version. Create a draft from the checked-in example, inspect it, and activate it explicitly:

```sh
docker compose --profile cli run --rm oneshot \
  policy-create --file /app/schemas/examples/execution-policy.json
docker compose --profile cli run --rm oneshot \
  policy-show --name local-readonly --version v1
docker compose --profile cli run --rm oneshot \
  policy-activate --name local-readonly --version v1
```

Use `policy-list`, `policy-disable`, or `policy-retire` for the remaining lifecycle operations. A retired revision cannot be reactivated. `autonomous-execute --job JOB_ID --target URL --policy NAME --idempotency-key TRIGGER_ID` creates and advances the same durable workflow used by the Command Center. Inspect or act on a paused workflow with `automation-get`, `automation-review`, and `automation-cancel`.

## Run a reviewed browser test

Planning does not create runnable selectors automatically. A person must add concrete actions and assertions to an execution manifest.

### 1. Choose the target application

The example below assumes the application is running on your Mac at port 3000. Containers reach it as `host.docker.internal`:

```sh
export TARGET_ORIGIN=http://host.docker.internal:3000
```

### 2. Create a draft manifest

```sh
docker compose --profile cli run --rm oneshot \
  prepare-execution --job JOB_ID --target "$TARGET_ORIGIN" > execution.json
```

### 3. Add reviewed steps

Open `execution.json`. Keep the tests you want to run and add steps like these:

```json
{
  "testCaseId": "TC-1",
  "steps": [
    { "action": "goto", "selector": null, "value": "/" },
    { "action": "click", "selector": "button[type=submit]", "value": null },
    { "action": "expectText", "selector": "h1", "value": "Welcome" }
  ]
}
```

Supported actions are `goto`, `click`, `fill`, `expectText`, `expectVisible`, and `expectUrl`. Every selected test needs at least one assertion.

### 4. Hash the exact reviewed file

```sh
export REVIEWED_SHA256=$(shasum -a 256 execution.json | awk '{print $1}')
```

If you edit `execution.json` again, calculate a new hash and review it again.

### 5. Execute it

```sh
docker compose --profile cli run --rm \
  -e Quality__Execution__AllowedOrigins="$TARGET_ORIGIN" \
  -v "$PWD/execution.json:/work/execution.json:ro" \
  oneshot execute --job JOB_ID \
  --manifest /work/execution.json \
  --sha256 "$REVIEWED_SHA256"
```

Copy the returned `RUN_ID`.

### 6. Read the result

```sh
docker compose --profile cli run --rm oneshot \
  get-run --id RUN_ID
```

Execution status is separate from planning status. A run may be `Passed`, `Failed`, `TimedOut`, `Cancelled`, or `InfrastructureFailed`.

For network restrictions, evidence files, and Linux target addressing, read [reviewed Playwright execution](docs/playwright-execution.md).

## Optional tools

### Store evidence in MinIO

Set this in `.env`:

```dotenv
QUALITY_ARTIFACTS_MODE=MinIO
QUALITY_ARTIFACTS_BUCKET=quality-artifacts
QUALITY_ARTIFACTS_RETENTION_DAYS=30
```

Reload the services and initialize the bucket once:

```sh
docker compose up --no-build --force-recreate -d --wait api worker
docker compose --profile cli run --rm oneshot init-artifacts
```

Future executions upload evidence automatically. Retry an upload with:

```sh
docker compose --profile cli run --rm oneshot \
  publish-artifacts --run RUN_ID
```

Open the MinIO console at [http://127.0.0.1:9001](http://127.0.0.1:9001). See [artifact storage](docs/minio-artifacts.md) for retention and security details.

Azure Blob Storage is also supported. Set `QUALITY_ARTIFACTS_MODE=Azure`, configure either
`AZURE_STORAGE_CONNECTION_STRING` or `QUALITY_ARTIFACTS_AZURE_SERVICE_URI`, and set
`QUALITY_ARTIFACTS_AZURE_CONTAINER`. Run the same `init-artifacts` command once to create the private
container. Passwordless service-URI configuration uses `DefaultAzureCredential`; account lifecycle
retention remains an Azure administration responsibility. See [Azure artifact storage](docs/azure-artifacts.md).

The Command Center evidence list can safely preview text, JSON, and images and save every artifact type.
Reads use the local execution copy first and fall back to the configured remote provider only through the
run's stored artifact association.

### Classify a failed run

In the Command Center, select the job, find the failed run under **Execution runs**, inspect its evidence references, choose a classification, enter a reason, and select **Save human review**.

The equivalent terminal command remains available:

```sh
docker compose --profile cli run --rm oneshot \
  classify-failure --run RUN_ID \
  --classification ApplicationFailure \
  --reason "Confirmed against the acceptance criterion"
```

Allowed review classifications are `ApplicationFailure`, `TestFailure`, and `InfrastructureFailure`.

### Propose a passing test as regression coverage

Regression promotion adds a new Playwright test to the **configured target test repository**. It does not patch the application repository unless that repository is the configured target.

The easiest workflow is in the Command Center:

1. Select a job with a passing execution run.
2. Select **Create reviewable patch**.
3. Inspect the target repository, proposed files, exact Git patch, and patch SHA-256.
4. Confirm the review and select **Apply patch to target repository**.
5. Test and commit the new files from the target repository using its normal review process.

Applying through the Command Center adds files to the target repository's working tree. It does not commit, push, create a branch, or open a pull request.

The equivalent terminal command creates the patch proposal without applying it:

```sh
docker compose --profile cli run --rm oneshot \
  promote-regression --job JOB_ID --run RUN_ID \
  --sha256 "$REVIEWED_SHA256"
```

Follow [reviewed regression promotion](docs/regression-promotion.md) to inspect or apply the patch and configure a separate test repository.

## Verify the installation

Run the checks inside the same application image:

```sh
python3 scripts/verify-compose.py
docker compose --profile test run --rm smoke
```

For repository development, install .NET 10.0.401+, Node.js 20+, npm dependencies, and Chromium, then run:

```sh
dotnet restore QualitySystem.sln --locked-mode
dotnet build QualitySystem.sln -c Release --no-restore
dotnet test QualitySystem.sln -c Release --no-build
npm ci
npm run build
npm test
npm run test:execution
```

## Troubleshooting

| Problem | What to do |
| --- | --- |
| Command Center says `API unavailable` | Run `docker compose ps`, then `docker compose logs api`. |
| A new token or key is ignored | Recreate the services with the restart command above. |
| Jira returns 401 or 403 | Check `JIRA_BASE_URL`, `JIRA_EMAIL`, and the Jira API token in `.env`. |
| Planning ends with `planning_timeout` | Use one 60-second attempt as shown in the `.env` example above. |
| Planning returns no tests | Review acceptance criteria and coverage gaps; missing or synthetic criteria intentionally produce a gap report. |
| Execution rejects the target | Make `Quality__Execution__AllowedOrigins` exactly match the manifest origin, including its port. |
| Docker cannot find `docker-credential-desktop` on macOS | Add `/Applications/Docker.app/Contents/Resources/bin` to `PATH`. |
| Port 5080 or 5081 is already used | Stop the other process or change the host-side port in `docker-compose.yml`. |

## What each part does

| Path | Purpose |
| --- | --- |
| `src/Quality.CommandCenter` | Local web interface and server-side API proxy |
| `src/Quality.Api` | API, worker, and terminal entry point |
| `src/Quality.Orchestrator` | Requirement and planning workflow |
| `src/Quality.Persistence` | PostgreSQL and file storage |
| `playwright/execution` | Reviewed manifest runner |
| `data/executions` | Native execution results and evidence |

More detail: [architecture](docs/architecture.md), [Command Center security](docs/command-center.md), [API contract](docs/job-contract.md), and [current build plan](docs/next-steps.md).
