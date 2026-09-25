using Quality.Domain;
namespace Quality.Orchestrator;

public interface IRequirementSource
{
    Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct);
}
public interface ILlmProvider
{
    string Name { get; }
    Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct);
}
public sealed record ArtifactHandle(string Key, string ContentType, long Length, string Sha256 = "", string Bucket = "", string Provider = "");
public interface IArtifactStore
{
    string Provider { get; }
    Task<ArtifactHandle> PutAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
}
public sealed record ArtifactStoreInfo(string Provider, string Container, string Prefix, int? RetentionDays, bool RetentionManaged);
public interface IRemoteArtifactStore : IArtifactStore
{
    ArtifactStoreInfo Info { get; }
    Task InitializeAsync(CancellationToken ct);
}
public sealed record SourceChange(string Path, string Content);
public interface ISourceControl
{
    Task<string> ProposeAsync(string branch, string title, IReadOnlyList<SourceChange> changes, CancellationToken ct);
}
public sealed record RegressionProposal(string RunId, string Title, string Status, string PatchSha256,
    string TargetRepository, IReadOnlyList<SourceChange> Files, string Patch, DateTimeOffset? AppliedAt = null);
public interface IRegressionProposalManager
{
    Task<RegressionProposal?> GetAsync(string runId, CancellationToken ct);
    Task<RegressionProposal> ApplyAsync(string runId, string reviewedPatchSha256, CancellationToken ct);
}
public interface ITestExecutor
{
    Task<TestRun> ExecuteAsync(TestPlan plan, Uri baseUrl, CancellationToken ct);
}
public interface IJobStore
{
    Task InitializeAsync(CancellationToken ct);
    Task CreateAsync(QualityJob job, CancellationToken ct);
    Task<QualityJob> CreateOrGetAsync(QualityJob job, CancellationToken ct);
    Task<QualityJob?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<QualityJob>> ListAsync(int limit, DateTimeOffset? beforeCreatedAt, string? beforeId, CancellationToken ct);
    Task<QualityJob?> CancelAsync(string id, CancellationToken ct);
    // A claim consumes a worker attempt atomically; exhaustion returns a persisted terminal job.
    Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct);
    // Renewal changes only lease expiry and revision, and must not revive an expired claim.
    Task<QualityJob> RenewLeaseAsync(QualityJob job, TimeSpan lease, CancellationToken ct);
    // A save must fence both revision and lease ownership; expired workers cannot overwrite a newer attempt.
    Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct);
}
public interface IExecutionRequestStore
{
    Task CreateAsync(ExecutionRequest request, CancellationToken ct);
    Task<ExecutionRequest?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ExecutionRequest>> ListByJobAsync(string jobId, CancellationToken ct);
    Task<ExecutionRequest?> CancelAsync(string id, DateTimeOffset now, CancellationToken ct);
    // Atomically reserves lifetime, rolling-window, and concurrency budget for one approved request.
    Task<ExecutionRequest?> TryReserveAutoLaunchAsync(string id, long expectedRevision, AutoLaunchBudget budget,
        DateTimeOffset now, CancellationToken ct);
    Task<ExecutionRequest?> ClaimAsync(TimeSpan lease, CancellationToken ct);
    Task<ExecutionRequest> SaveAsync(ExecutionRequest request, long expectedRevision, CancellationToken ct);
}
public sealed record AutoLaunchBudget(string PolicyHash, int MaximumLifetime, int MaximumConcurrent,
    int WindowSeconds, int MaximumInWindow);
public interface IExecutionPolicyStore
{
    Task InitializeAsync(CancellationToken ct);
    Task<IReadOnlyList<ExecutionPolicyRevision>> ListAsync(CancellationToken ct);
    Task<ExecutionPolicyRevision?> GetAsync(string name, string version, CancellationToken ct);
    Task<ExecutionPolicyRevision> CreateAsync(ExecutionPolicy policy, bool activate, DateTimeOffset now, CancellationToken ct);
    Task<ExecutionPolicyRevision> SetStatusAsync(string name, string version, ExecutionPolicyStatus status, DateTimeOffset now, CancellationToken ct);
}
public interface IAutomationWorkflowStore
{
    Task<AutomationWorkflow> CreateOrGetAsync(AutomationWorkflow workflow, CancellationToken ct);
    Task<AutomationWorkflow?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<AutomationWorkflow>> ListByJobAsync(string jobId, CancellationToken ct);
    Task<AutomationWorkflow?> ClaimAsync(TimeSpan lease, CancellationToken ct);
    Task<AutomationWorkflow> SaveAsync(AutomationWorkflow workflow, long expectedRevision, string leaseToken, CancellationToken ct);
    Task<AutomationWorkflow> SaveReviewAsync(AutomationWorkflow workflow, long expectedRevision, CancellationToken ct);
    Task<AutomationWorkflow?> CancelAsync(string id, DateTimeOffset now, CancellationToken ct);
}
public sealed class ExecutionRequestConflictException() : Exception("Execution request changed; reload before retrying");
public sealed class ExecutionPolicyConflictException() : Exception("Execution policy revision already exists with different content or state");
public sealed class AutomationWorkflowConflictException() : Exception("Automation workflow changed; reload before retrying");
public sealed class IdempotencyConflictException() : Exception("Idempotency key was already used for a different request");
public sealed class LeaseLostException() : Exception("Job lease expired or ownership changed");
