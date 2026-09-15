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
    Task<QualityJob?> GetAsync(string id, CancellationToken ct);
    Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct);
    // A save must fence both revision and lease ownership; expired workers cannot overwrite a newer attempt.
    Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct);
}
public sealed class LeaseLostException() : Exception("Job lease expired or ownership changed");
