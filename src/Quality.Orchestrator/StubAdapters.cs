using Quality.Domain;
namespace Quality.Orchestrator;

public sealed class StubRequirementSource : IRequirementSource
{
    public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        reference.Validate();
        var id = $"{reference.Source}:{reference.Id}";
        return Task.FromResult(new Requirement(id, reference, $"Stub requirement for {reference.Id}",
            "Synthetic requirement. No external provider was contacted.", ["Test user"],
            ["A dedicated test environment is available"],
            [new("AC-1", "The requested feature reports a successful result")],
            ["Actual acceptance criteria have not been imported"], "stub-v1", true));
    }
}
public sealed class StubLlmProvider : ILlmProvider
{
    public string Name => "stub";
    public Task<TestPlan> PlanAsync(Requirement requirement, string promptVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new TestPlan(Guid.NewGuid().ToString("N"), requirement.Id,
            "Synthetic structured plan; review before generating executable tests.",
            [new("TC-1", requirement.Id, "Feature happy path", "HappyPath", "P1",
                requirement.AcceptanceCriteria.Select(a => a.Id).ToArray(),
                [new("Perform the documented feature action", "A successful result is shown")])],
            ["Stub criteria are placeholders"], ["Negative and regression coverage await real requirements"],
            promptVersion, true));
    }
}
// Fail closed: placeholders never pretend that an artifact was uploaded, code published, or a test passed.
public sealed class StubArtifactStore : IArtifactStore
{
    public Task<ArtifactHandle> PutAsync(string key, Stream content, string contentType, CancellationToken ct)
        => throw new NotSupportedException("Artifact provider is not configured");
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
        => throw new NotSupportedException("Artifact provider is not configured");
}
public sealed class StubSourceControl : ISourceControl
{
    public Task<string> ProposeAsync(string branch, string title, IReadOnlyList<SourceChange> changes, CancellationToken ct)
        => throw new NotSupportedException("Source control provider is not configured");
}
public sealed class StubTestExecutor : ITestExecutor
{
    public Task<TestRun> ExecuteAsync(TestPlan plan, Uri baseUrl, CancellationToken ct)
        => throw new NotSupportedException("Executor is not connected; run workspace smoke tests separately");
}
