namespace Quality.Orchestrator;

/// <summary>Coordinates seeded and operator-authored policies without treating browser input as executable configuration.</summary>
public sealed class ExecutionPolicyService(ExecutionPolicyCatalog catalog, FileExecutionPolicyStore store, IEnumerable<ExecutionPolicy> seeded)
{
    private readonly ExecutionPolicyCatalog catalog = catalog;
    private readonly FileExecutionPolicyStore store = store;
    private readonly ExecutionPolicy[] seeded = seeded.ToArray();

    public async Task InitializeAsync(CancellationToken ct)
    {
        var saved = await store.LoadAsync(ct);
        foreach (var policy in seeded.Concat(saved).GroupBy(policy => policy.Name, StringComparer.Ordinal).Select(group => group.Last()))
            catalog.Upsert(policy);
        if (saved.Count == 0) await store.SaveAsync(catalog.Snapshot(), ct);
    }

    public IReadOnlyList<object> Describe() => catalog.Describe();

    public async Task SaveAsync(ExecutionPolicy policy, CancellationToken ct)
    {
        catalog.Upsert(policy);
        await store.SaveAsync(catalog.Snapshot(), ct);
    }
}
