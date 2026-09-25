using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed class AutomationWorkflowService(
    IAutomationWorkflowStore workflows,
    IJobStore jobs,
    ExecutionRequestService executions,
    IUiInspector inspector,
    ITestRunStore runs,
    ExecutionPolicyCatalog policies,
    TimeProvider clock)
{
    private const int MaximumStageAttempts = 3;

    public async Task<AutomationWorkflow> CreateAsync(string jobId, string target, string policyName,
        string? idempotencyKey, CancellationToken ct)
    {
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new ArgumentException("A valid planning job is required");
        _ = await RequiredJobAsync(jobId, ct);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Automation target must be an absolute HTTP(S) URL without credentials or fragment");
        var policy = policies.Required(policyName);
        if (!policy.NonProduction) throw new ArgumentException("Automation workflows require a non-production policy");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!policy.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Contains(origin, StringComparer.Ordinal))
            throw new ArgumentException("Target is not allowed by the automation policy");
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey.Trim();
        if (idempotencyKey.Length is < 1 or > 200 || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Idempotency key must contain 1-200 non-control characters");
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var keyHash = Hash(idempotencyKey);
        var inputHash = Hash(JsonSerializer.Serialize(new { jobId, target = uri.AbsoluteUri,
            policy = policy.Fingerprint() }, ContractJson.Options));
        var created = new AutomationWorkflow(id, keyHash, inputHash, jobId, uri.AbsoluteUri,
            AutomationWorkflowStatus.Triggered, policy.Name, policy.Version, policy.Fingerprint(),
            policy.CanonicalJson(), policy.Environment, 0, now, now,
            [new(AutomationWorkflowStatus.Triggered, now, StageKey(id, AutomationWorkflowStatus.Triggered))]);
        return await workflows.CreateOrGetAsync(created, ct);
    }

    public Task<AutomationWorkflow?> GetAsync(string id, CancellationToken ct) => workflows.GetAsync(id, ct);
    public Task<IReadOnlyList<AutomationWorkflow>> ListByJobAsync(string jobId, CancellationToken ct)
        => workflows.ListByJobAsync(jobId, ct);

    public async Task<AutomationWorkflow?> ProcessNextAsync(CancellationToken ct)
    {
        var claimed = await workflows.ClaimAsync(TimeSpan.FromMinutes(10), ct);
        if (claimed is null) return null;
        try
        {
            var advanced = await AdvanceAsync(claimed, ct);
            return await workflows.SaveAsync(advanced, claimed.Revision, claimed.LeaseToken!, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var now = clock.GetUtcNow();
            var failed = claimed.StageAttempts >= MaximumStageAttempts;
            var updated = claimed with
            {
                Status = failed ? AutomationWorkflowStatus.Failed : claimed.Status,
                UpdatedAt = now,
                Error = ex.GetType().Name,
                EscalationReason = failed ? "stage_retry_budget_exhausted" : claimed.EscalationReason,
                NextAttemptAt = failed ? null : now.AddSeconds(Math.Pow(2, claimed.StageAttempts - 1)),
                Checkpoints = failed ? [.. claimed.Checkpoints,
                    new(AutomationWorkflowStatus.Failed, now, StageKey(claimed.Id, AutomationWorkflowStatus.Failed), ex.GetType().Name)]
                    : claimed.Checkpoints
            };
            try { return await workflows.SaveAsync(updated, claimed.Revision, claimed.LeaseToken!, CancellationToken.None); }
            catch (AutomationWorkflowConflictException) { return await workflows.GetAsync(claimed.Id, CancellationToken.None); }
        }
    }

    public async Task<AutomationWorkflow> ReviewAsync(string id, long revision, bool approve, string reviewer, CancellationToken ct)
    {
        reviewer = reviewer.Trim();
        if (reviewer.Length is < 1 or > 200 || reviewer.Any(char.IsControl))
            throw new ArgumentException("Reviewer must contain 1-200 non-control characters");
        var current = await workflows.GetAsync(id, ct) ?? throw new ArgumentException("Automation workflow not found");
        if (current.Revision != revision) throw new AutomationWorkflowConflictException();
        if (current.Status != AutomationWorkflowStatus.AwaitingReview)
            throw new ArgumentException("Workflow is not awaiting review");
        var now = clock.GetUtcNow();
        if (!approve)
        {
            var rejected = Transition(current with { ReviewDecision = "Rejected", Reviewer = reviewer,
                ReviewedAt = now, Error = "review_rejected" }, AutomationWorkflowStatus.Failed, now, "review_rejected");
            return await workflows.SaveReviewAsync(rejected, revision, ct);
        }
        if (current.ExecutionRequestId is null) throw new InvalidOperationException("Workflow execution request is missing");
        var request = await executions.GetAsync(current.ExecutionRequestId, ct)
            ?? throw new InvalidOperationException("Workflow execution request is missing");
        if (request.Status == ExecutionRequestStatus.AwaitingApproval)
            request = await executions.ApproveAsync(request.Id, request.Revision, request.ManifestHash, reviewer, ct);
        if (request.Status != ExecutionRequestStatus.Approved)
            throw new ArgumentException("Workflow execution request cannot be reviewed in its current state");
        var approved = Transition(current with { ReviewDecision = "Approved", Reviewer = reviewer,
            ReviewedAt = now, EscalationReason = null }, AutomationWorkflowStatus.Approved, now, "human_review_approved");
        return await workflows.SaveReviewAsync(approved, revision, ct);
    }

    public async Task<AutomationWorkflow?> CancelAsync(string id, CancellationToken ct)
    {
        var current = await workflows.GetAsync(id, ct);
        if (current?.ExecutionRequestId is { } requestId)
            await executions.CancelAsync(requestId, ct);
        return await workflows.CancelAsync(id, clock.GetUtcNow(), ct);
    }

    private async Task<AutomationWorkflow> AdvanceAsync(AutomationWorkflow workflow, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        switch (workflow.Status)
        {
            case AutomationWorkflowStatus.Triggered:
            {
                var job = await RequiredJobAsync(workflow.JobId, ct);
                return Transition(workflow, AutomationWorkflowStatus.Planned, now, job.TestPlan!.Id);
            }
            case AutomationWorkflowStatus.Planned:
            {
                var result = await inspector.InspectAsync(new Uri(workflow.Target, UriKind.Absolute), ct);
                if (result.Target != workflow.Target) throw new InvalidOperationException("Inspection target changed");
                return Transition(workflow with { InspectionJson = JsonSerializer.Serialize(result, ContractJson.Options) },
                    AutomationWorkflowStatus.Inspected, now, Hash(JsonSerializer.Serialize(result, ContractJson.Options)));
            }
            case AutomationWorkflowStatus.Inspected:
            {
                var job = await RequiredJobAsync(workflow.JobId, ct);
                var inspection = JsonSerializer.Deserialize<UiInspection>(workflow.InspectionJson!, ContractJson.Options)
                    ?? throw new InvalidOperationException("Inspection checkpoint is invalid");
                var manifest = ReviewedManifestBuilder.Build(job.TestPlan!, workflow.Target, inspection);
                var json = JsonSerializer.Serialize(manifest, ContractJson.Options);
                return Transition(workflow with { ManifestJson = json,
                    ManifestHash = ExecutionManifest.Hash(Encoding.UTF8.GetBytes(json)) },
                    AutomationWorkflowStatus.ManifestPrepared, now, ExecutionManifest.Hash(Encoding.UTF8.GetBytes(json)));
            }
            case AutomationWorkflowStatus.ManifestPrepared:
            {
                var policy = Policy(workflow);
                var manifest = JsonSerializer.Deserialize<ExecutionManifest>(workflow.ManifestJson!, ContractJson.Options)
                    ?? throw new InvalidOperationException("Manifest checkpoint is invalid");
                policy.ValidateManifest(manifest, requireAutoApproval: false);
                return Transition(workflow, AutomationWorkflowStatus.PolicyEvaluated, now, policy.Fingerprint());
            }
            case AutomationWorkflowStatus.PolicyEvaluated:
                return await PrepareRequestAsync(workflow, now, ct);
            case AutomationWorkflowStatus.Approved:
                return await QueueAsync(workflow, now, ct);
            case AutomationWorkflowStatus.Queued:
            {
                var request = await RequiredRequestAsync(workflow, ct);
                if (!Terminal(request.Status)) return Release(workflow, now);
                return Transition(workflow with { RunId = request.RunId, Error = request.Error },
                    AutomationWorkflowStatus.Executed, now, request.Status.ToString());
            }
            case AutomationWorkflowStatus.Executed:
            {
                var run = workflow.RunId is null ? null : await runs.GetAsync(workflow.RunId, ct);
                var detail = run?.ArtifactUploadStatus ?? (workflow.RunId is null ? "no_run_evidence" : "local_evidence");
                return Transition(workflow, AutomationWorkflowStatus.EvidencePublished, now, detail);
            }
            case AutomationWorkflowStatus.EvidencePublished:
            {
                var run = workflow.RunId is null ? null : await runs.GetAsync(workflow.RunId, ct);
                var classification = run?.FailureClassification ?? "InfrastructureFailure";
                return Transition(workflow with { Classification = classification },
                    AutomationWorkflowStatus.Classified, now, classification);
            }
            case AutomationWorkflowStatus.Classified:
                return Transition(workflow, AutomationWorkflowStatus.Completed, now, workflow.Classification);
            default:
                return Release(workflow, now);
        }
    }

    private async Task<AutomationWorkflow> PrepareRequestAsync(AutomationWorkflow workflow, DateTimeOffset now, CancellationToken ct)
    {
        var policy = Policy(workflow);
        var request = await executions.CreateAsync(workflow.JobId, workflow.Target, ct, policy, workflow.Id);
        if (request.Status == ExecutionRequestStatus.Draft)
            request = await executions.UpdateManifestAsync(request.Id, request.Revision,
                Encoding.UTF8.GetBytes(workflow.ManifestJson!), ct);
        var updated = workflow with { ExecutionRequestId = request.Id };
        if (request.Status == ExecutionRequestStatus.AwaitingApproval && !policy.AutoApprove)
            return Transition(updated with { EscalationReason = "policy_requires_review" },
                AutomationWorkflowStatus.AwaitingReview, now, "policy_requires_review");
        if (request.Status == ExecutionRequestStatus.AwaitingApproval)
            request = await executions.ApproveAsync(request.Id, request.Revision, request.ManifestHash,
                policy.ReviewerIdentity(), ct);
        if (request.Status == ExecutionRequestStatus.Approved)
            return Transition(updated, AutomationWorkflowStatus.Approved, now, request.ManifestHash);
        if (request.Status is ExecutionRequestStatus.Queued or ExecutionRequestStatus.Running)
            return Transition(updated, AutomationWorkflowStatus.Queued, now, request.Status.ToString());
        if (Terminal(request.Status))
            return Transition(updated with { RunId = request.RunId }, AutomationWorkflowStatus.Executed, now, request.Status.ToString());
        throw new InvalidOperationException("Execution request cannot be reconciled");
    }

    private async Task<AutomationWorkflow> QueueAsync(AutomationWorkflow workflow, DateTimeOffset now, CancellationToken ct)
    {
        var policy = Policy(workflow);
        var request = await RequiredRequestAsync(workflow, ct);
        if (request.Status is ExecutionRequestStatus.Queued or ExecutionRequestStatus.Running)
            return Transition(workflow, AutomationWorkflowStatus.Queued, now, request.Status.ToString());
        if (Terminal(request.Status))
            return Transition(workflow with { RunId = request.RunId }, AutomationWorkflowStatus.Executed, now, request.Status.ToString());
        if (request.Status != ExecutionRequestStatus.Approved)
            throw new InvalidOperationException("Approved workflow request is not approved");
        if (workflow.ReviewDecision != "Approved")
        {
            if (!policy.AutoLaunch || policy.CanaryMaxAutoLaunches < 1)
                return Transition(workflow with { EscalationReason = "policy_requires_launch_review" },
                    AutomationWorkflowStatus.AwaitingReview, now, "policy_requires_launch_review");
            var reserved = await executions.ReserveAutoLaunchAsync(request, policy, ct);
            if (reserved is null)
                return Transition(workflow with { EscalationReason = "auto_launch_budget_exhausted" },
                    AutomationWorkflowStatus.AwaitingReview, now, "auto_launch_budget_exhausted");
            request = reserved;
        }
        request = await executions.LaunchAsync(request.Id, request.Revision, ct);
        return Transition(workflow, AutomationWorkflowStatus.Queued, now, request.Status.ToString());
    }

    private async Task<ExecutionRequest> RequiredRequestAsync(AutomationWorkflow workflow, CancellationToken ct)
        => workflow.ExecutionRequestId is { } id && await executions.GetAsync(id, ct) is { } request
            ? request : throw new InvalidOperationException("Workflow execution request is missing");

    private async Task<QualityJob> RequiredJobAsync(string id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        return job?.Status == JobStatus.Completed && job.TestPlan is not null
            ? job : throw new ArgumentException("A completed planning job is required");
    }

    private static ExecutionPolicy Policy(AutomationWorkflow workflow)
    {
        var policy = JsonSerializer.Deserialize<ExecutionPolicy>(workflow.PolicySnapshot, ContractJson.Options)
            ?? throw new InvalidOperationException("Policy snapshot is invalid");
        if (policy.Fingerprint() != workflow.PolicyHash || policy.Name != workflow.PolicyName
            || policy.Version != workflow.PolicyVersion || policy.Environment != workflow.Environment)
            throw new InvalidOperationException("Policy snapshot integrity check failed");
        return policy;
    }

    private static AutomationWorkflow Transition(AutomationWorkflow workflow, AutomationWorkflowStatus status,
        DateTimeOffset now, string? detail = null) => workflow with
        {
            Status = status,
            UpdatedAt = now,
            StageAttempts = 0,
            NextAttemptAt = null,
            Error = status == AutomationWorkflowStatus.Failed ? workflow.Error : null,
            Checkpoints = [.. workflow.Checkpoints, new(status, now, StageKey(workflow.Id, status), detail)]
        };

    private static AutomationWorkflow Release(AutomationWorkflow workflow, DateTimeOffset now)
        => workflow with { UpdatedAt = now, StageAttempts = 0, NextAttemptAt = now.AddMilliseconds(250) };
    private static bool Terminal(ExecutionRequestStatus status) => status is ExecutionRequestStatus.Passed
        or ExecutionRequestStatus.Failed or ExecutionRequestStatus.TimedOut
        or ExecutionRequestStatus.InfrastructureFailed or ExecutionRequestStatus.Cancelled;
    private static string StageKey(string id, AutomationWorkflowStatus status) => Hash($"{id}:{status}");
    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
