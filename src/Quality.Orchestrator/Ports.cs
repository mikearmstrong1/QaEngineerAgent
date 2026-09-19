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
public sealed record ArtifactHandle(string Key, string ContentType, long Length, string Sha256 = "", string Bucket = "");
public interface IArtifactStore
{
    Task<ArtifactHandle> PutAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
}
public sealed record SourceChange(string Path, string Content);
public interface ISourceControl
{
    Task<string> ProposeAsync(string branch, string title, IReadOnlyList<SourceChange> changes, CancellationToken ct);
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
public sealed class IdempotencyConflictException() : Exception("Idempotency key was already used for a different request");
public sealed class LeaseLostException() : Exception("Job lease expired or ownership changed");
