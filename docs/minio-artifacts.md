# MinIO artifact storage

Step 4 adds an S3-compatible MinIO artifact store. `Local` remains the default: execution keeps its existing local files and makes no upload request. `MinIO` mode uploads finished execution evidence and records bucket/object references without changing the browser result.

## Native setup

Configure a dedicated, unversioned bucket and an account with access to it:

```sh
export Quality__Artifacts__Mode=MinIO
export Quality__Artifacts__Endpoint=http://127.0.0.1:9000
export Quality__Artifacts__AccessKey='<access key>'
export Quality__Artifacts__SecretKey='<secret key>'
export Quality__Artifacts__Bucket=quality-artifacts
export Quality__Artifacts__RetentionDays=30

dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll init-artifacts
```

`init-artifacts` creates a missing bucket and installs/updates the `quality-system-retention` lifecycle rule, scoped only to `quality-system/`. Other lifecycle rules are preserved. A conflicting rule ID under a different prefix and versioned/suspended buckets are rejected. Ordinary uploads do not create buckets or change retention. Run initialization as an administrative setup step; upload credentials then need object read/write access, not bucket administration. No public access policy is created.

Retention expires objects under this prefix after the configured number of days, including existing matching objects. Deletion is performed asynchronously by MinIO's lifecycle processing. It does not delete local run files or evidence-association records. Those records may outlive remote evidence; choose the retention period accordingly. Coordinate lifecycle administration because the S3 API replaces a bucket's lifecycle document in a read/modify/write operation.

## Upload and retry

In MinIO mode, the `execute` CLI automatically publishes artifacts after saving its terminal browser result. Cancelled commands retain local files for a later explicit upload. To publish a previous run or retry a storage failure without rerunning tests:

```sh
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll publish-artifacts --run RUN_ID
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll get-run --id RUN_ID
```

`artifactKeys` retain their local paths. `storedArtifacts` contains each local key, object key, bucket, provider, content type, byte length and SHA-256 digest. `artifactUploadStatus` is separate: `Uploading`, `Uploaded`, or `Failed` (null before publishing). A failed upload leaves the test status and successful associations intact. `execute` exits 1 when storage fails, even if the test status is `Passed`; `publish-artifacts` exits 0 only after all listed artifacts are uploaded.

Each remote key has the form `quality-system/runs/RUN_ID/SHA256/relative-path`. Retrying re-uploads the same bytes to the same key, covering lost acknowledgements and expired objects. It does not create duplicate association entries. Changed content gets a different key. A per-run file lock serializes publishers. Failure after an object upload but before saving its association is recoverable by rerunning the upload command.

The publisher uploads files listed in the saved run, including traces, screenshots, logs, JSON reports, manifest and generated test inputs. It rejects traversal and symbolic links in evidence paths. MIME types are assigned by extension, with `application/octet-stream` as fallback. Uploads retain any sensitive data already present in browser evidence, including form values; configure the bucket and its access accordingly.

The S3 SDK sends a SHA-256 checksum and `sha256` object metadata. The store's `OpenReadAsync` verifies downloaded bytes against that metadata before returning a temporary stream; missing or mismatched checksums fail. The returned stream deletes its temporary file when disposed. An ETag is not treated as a checksum.

## Associate failure evidence

A prepared `FailureAnalysis` JSON can reference local artifact keys from its run, or that run's uploaded object keys:

```sh
dotnet src/Quality.Api/bin/Release/net10.0/Quality.Api.dll associate-failure --file analysis.json
```

The analysis must have a new 32-character GUID ID, a valid `testRunId`, classification, summary, confidence in [0,1], and nonempty `evidenceArtifactKeys`. The command resolves only successfully uploaded evidence belonging to that run, rewrites `evidenceArtifactKeys` to object keys, and adds full `evidenceArtifacts` metadata. It saves an immutable association at `RUN_ID/analyses/ANALYSIS_ID.json` and returns the JSON. It does not generate a diagnosis or upload the analysis itself. Automated failure classification remains a later step.

## Limits and configuration

| Setting under `Quality__Artifacts__` | Default |
| --- | --- |
| `Mode` | `Local`; set `MinIO` for uploads |
| `Endpoint` | Required in MinIO mode; HTTP(S) origin |
| `AccessKey`, `SecretKey` | Required in MinIO mode |
| `Bucket` | `quality-artifacts` |
| `RetentionDays` | 30; allowed 1–3650 |
| `MaxArtifactBytes` | 128 MiB; allowed 1 byte–1 GiB |
| `TimeoutSeconds` | 60 per store operation; allowed 1–300 |

An upload uses a bounded temporary disk snapshot instead of buffering an entire artifact in memory. The SDK allows up to two retries within the operation deadline. A run publication has a five-minute overall deadline. Raw provider error text is not saved in the run. Run status and partial upload associations remain durable across a failed attempt; restart recovery for abandoned `Uploading` states is manual via `publish-artifacts`.

## Compose

Rebuild the image after this change. `.env` accepts:

```sh
QUALITY_ARTIFACTS_MODE=MinIO
QUALITY_ARTIFACTS_BUCKET=quality-artifacts
QUALITY_ARTIFACTS_RETENTION_DAYS=30
```

Compose forwards the internal MinIO endpoint and development credentials. With MinIO running, initialize once using:

```sh
docker compose --profile cli run --rm oneshot init-artifacts
```

The existing MinIO volume retains objects, and the execution volume retains local evidence and associations. The default remains `Local` so existing demos do not require bucket initialization.

## Verification

```sh
dotnet build QualitySystem.sln -c Release
dotnet test QualitySystem.sln -c Release
python3 scripts/verify-artifacts.py
```

The last command reads the local Compose connection configuration without printing credentials, creates a uniquely named temporary bucket, verifies the CLI and store, then removes only that test bucket. For another test endpoint, set `QUALITY_TEST_MINIO_ENDPOINT`, `QUALITY_TEST_MINIO_ACCESS_KEY`, and `QUALITY_TEST_MINIO_SECRET_KEY` when running .NET tests. Bucket creation/lifecycle/object/deletion permissions are required for this integration test. The test verifies the retention rule configuration; it does not wait days for expiration.

References: [AWS S3 SDK for .NET](https://www.nuget.org/packages/AWSSDK.S3/), [lifecycle configuration](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutLifecycleConfigurationRequest.html).
