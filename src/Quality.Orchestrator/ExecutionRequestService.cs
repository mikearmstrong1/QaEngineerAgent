using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed class ExecutionRequestService(
    IExecutionRequestStore requests,
    IJobStore jobs,
    IReviewedTestExecutor executor,
    ExecutionOptions options,
    TimeProvider clock,
    RunArtifactPublisher? publisher = null,
    IArtifactStore? artifactStore = null)
{
    private static readonly JsonSerializerOptions StrictJson = new(ContractJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<ExecutionRequest> CreateAsync(string jobId, string target, CancellationToken ct)
    {
        var job = await CompletedJobAsync(jobId, ct);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) || targetUri.Scheme is not ("http" or "https")
            || targetUri.UserInfo.Length != 0 || targetUri.Fragment.Length != 0)
            throw new ArgumentException("Execution target must be an absolute HTTP(S) URL without credentials or fragment");
        var origin = targetUri.GetLeftPart(UriPartial.Authority);
        ExecutionManifest.ValidateOrigin(origin);
        if (!options.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            throw new ArgumentException("Execution target is not allowlisted");
        var manifest = new ExecutionManifest(job.TestPlan!.Id, ExecutionManifest.HashPlan(job.TestPlan), target,
            job.TestPlan.TestCases.Select(test => new ExecutionTest(test.Id, [])).ToArray());
        var json = JsonSerializer.Serialize(manifest, ContractJson.Options);
        var now = clock.GetUtcNow();
        var request = new ExecutionRequest(Guid.NewGuid().ToString("N"), job.Id, job.TestPlan.Id, target,
            ExecutionRequestStatus.Draft, json, ExecutionManifest.Hash(Encoding.UTF8.GetBytes(json)), 0, now, now);
        await requests.CreateAsync(request, ct);
        return request;
    }

    public Task<ExecutionRequest?> GetAsync(string id, CancellationToken ct) => requests.GetAsync(id, ct);
    public Task<IReadOnlyList<ExecutionRequest>> ListByJobAsync(string jobId, CancellationToken ct)
        => requests.ListByJobAsync(jobId, ct);

    public async Task<ExecutionRequest> UpdateManifestAsync(string id, long revision, byte[] manifestBytes, CancellationToken ct)
    {
        if (manifestBytes.Length is 0 or > 1024 * 1024) throw new ArgumentException("Manifest must contain 1-1048576 bytes");
        var current = await RequiredAsync(id, ct);
        if (current.Revision != revision) throw new ExecutionRequestConflictException();
        if (current.Status is ExecutionRequestStatus.Running or ExecutionRequestStatus.Passed or ExecutionRequestStatus.Failed
            or ExecutionRequestStatus.TimedOut or ExecutionRequestStatus.InfrastructureFailed or ExecutionRequestStatus.Cancelled)
            throw new ArgumentException("A started execution request cannot be edited");
        var manifest = JsonSerializer.Deserialize<ExecutionManifest>(manifestBytes, StrictJson)
            ?? throw new ArgumentException("Manifest is required");
        var job = await CompletedJobAsync(current.JobId, ct);
        manifest.Validate(job.TestPlan!, options.AllowedOrigins);
        if (!string.Equals(manifest.Target, current.Target, StringComparison.Ordinal))
            throw new ArgumentException("Manifest target cannot change; create another execution request");
        var json = Encoding.UTF8.GetString(manifestBytes);
        var updated = current with
        {
            Status = ExecutionRequestStatus.AwaitingApproval,
            ManifestJson = json,
            ManifestHash = ExecutionManifest.Hash(manifestBytes),
            Approval = null,
            UpdatedAt = clock.GetUtcNow(),
            Error = null
        };
        return await requests.SaveAsync(updated, revision, ct);
    }

    public async Task<ExecutionRequest> ApproveAsync(string id, long revision, string reviewedHash, string reviewer, CancellationToken ct)
    {
        var current = await RequiredAsync(id, ct);
        if (current.Revision != revision) throw new ExecutionRequestConflictException();
        if (current.Status != ExecutionRequestStatus.AwaitingApproval)
            throw new ArgumentException("Only a validated manifest awaiting approval can be approved");
        if (!string.Equals(reviewedHash, current.ManifestHash, StringComparison.Ordinal))
            throw new ArgumentException("Reviewed SHA-256 does not match the current manifest");
        reviewer = reviewer.Trim();
        if (reviewer.Length is < 1 or > 200 || reviewer.Any(char.IsControl))
            throw new ArgumentException("Reviewer must contain 1-200 non-control characters");
        var approved = current with
        {
            Status = ExecutionRequestStatus.Approved,
            Approval = new(current.ManifestHash, reviewer, clock.GetUtcNow()),
            UpdatedAt = clock.GetUtcNow()
        };
        return await requests.SaveAsync(approved, revision, ct);
    }

    public async Task<ExecutionRequest> LaunchAsync(string id, long revision, CancellationToken ct)
    {
        var current = await RequiredAsync(id, ct);
        if (current.Revision != revision) throw new ExecutionRequestConflictException();
        if (current.Status != ExecutionRequestStatus.Approved || current.Approval?.ManifestHash != current.ManifestHash)
            throw new ArgumentException("The current manifest has not been approved");
        var bytes = Encoding.UTF8.GetBytes(current.ManifestJson);
        if (ExecutionManifest.Hash(bytes) != current.ManifestHash)
            throw new InvalidOperationException("Persisted manifest integrity check failed");
        return await requests.SaveAsync(current with
        {
            Status = ExecutionRequestStatus.Queued,
            UpdatedAt = clock.GetUtcNow(),
            Error = null
        }, revision, ct);
    }

    public async Task<ExecutionRequest?> ProcessNextAsync(CancellationToken ct)
    {
        // Covers the maximum 315-second runner window plus the publisher's five-minute budget and checkpoint overhead.
        var running = await requests.ClaimAsync(TimeSpan.FromMinutes(15), ct);
        if (running is null || running.Status != ExecutionRequestStatus.Running) return running;
        try
        {
            var job = await CompletedJobAsync(running.JobId, ct);
            var bytes = Encoding.UTF8.GetBytes(running.ManifestJson);
            if (running.Approval?.ManifestHash != running.ManifestHash || ExecutionManifest.Hash(bytes) != running.ManifestHash)
                throw new InvalidOperationException("Persisted manifest integrity check failed");
            var run = await executor.ExecuteReviewedAsync(job.TestPlan!, bytes, running.ManifestHash, ct);
            if (publisher is not null && artifactStore?.Provider != "Local")
                run = await publisher.PublishAsync(run.Id, ct);
            var status = Enum.TryParse<ExecutionRequestStatus>(run.Status, out var parsed) ? parsed : ExecutionRequestStatus.Failed;
            return await requests.SaveAsync(running with
            {
                Status = status,
                RunId = run.Id,
                UpdatedAt = clock.GetUtcNow(),
                Error = run.ArtifactUploadStatus == "Failed" ? "artifact_upload_failed" : null,
                LeaseToken = null,
                LeaseUntil = null
            }, running.Revision, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryFinishAsync(running, ExecutionRequestStatus.Cancelled, "execution_cancelled", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await TryFinishAsync(running, ExecutionRequestStatus.InfrastructureFailed, ex.GetType().Name, CancellationToken.None);
            throw;
        }
    }

    private async Task TryFinishAsync(ExecutionRequest running, ExecutionRequestStatus status, string error, CancellationToken ct)
    {
        try { await requests.SaveAsync(running with { Status = status, UpdatedAt = clock.GetUtcNow(), Error = error,
            LeaseToken = null, LeaseUntil = null }, running.Revision, ct); }
        catch (ExecutionRequestConflictException) { }
    }
    private async Task<ExecutionRequest> RequiredAsync(string id, CancellationToken ct)
        => await requests.GetAsync(id, ct) ?? throw new ArgumentException("Execution request not found");
    private async Task<QualityJob> CompletedJobAsync(string id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        return job?.Status == JobStatus.Completed && job.TestPlan is not null
            ? job : throw new ArgumentException("A completed planning job is required");
    }
}
