# Azure Blob artifact storage

Azure Blob Storage is an alternative remote evidence provider to MinIO. Local execution files remain the
canonical first read. Azure receives finished evidence under content-addressed blob names and is used as a
fallback when the local file is unavailable.

## Configuration

Set the provider and choose exactly one authentication method:

```dotenv
QUALITY_ARTIFACTS_MODE=Azure
QUALITY_ARTIFACTS_AZURE_CONTAINER=quality-artifacts

# Local development or an explicitly managed secret:
AZURE_STORAGE_CONNECTION_STRING=

# Recommended for hosted workloads with managed identity or workload identity:
QUALITY_ARTIFACTS_AZURE_SERVICE_URI=https://ACCOUNT.blob.core.windows.net/
```

The service-URI path uses `DefaultAzureCredential`. Grant the runtime identity the minimum blob data role
needed for the operation. `init-artifacts` requires permission to create the container; normal publishing and
viewing require blob write and read access. Keep connection strings out of source control, logs, browser
responses, and saved evidence.

Initialize the private container once:

```sh
docker compose --profile cli run --rm oneshot init-artifacts
```

Ordinary uploads never create containers or change account policy. Configure Azure lifecycle management
separately with a prefix match of `quality-artifacts/quality-system/` (replace the container name when
configured differently). Azure lifecycle policy is account-level management-plane state, so the application
does not overwrite it.

## Upload and integrity behavior

Blob names use `quality-system/runs/RUN_ID/SHA256/relative-path`. Uploads carry the original content type and
the SHA-256 as blob metadata. Reads download to a bounded temporary file and verify the digest before returning
bytes. The same maximum artifact size, operation timeout, retryable publication, partial-progress persistence,
and failure-evidence association behavior described in [MinIO artifact storage](minio-artifacts.md) applies.

The Command Center never receives Azure credentials. Its BFF requests an artifact from the authenticated API,
which verifies that the requested key belongs to the run. Text, JSON, and images can be viewed safely; every
artifact type can be saved as an attachment.
