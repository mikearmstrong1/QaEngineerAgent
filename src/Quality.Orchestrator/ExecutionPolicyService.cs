namespace Quality.Orchestrator;

/// <summary>Coordinates immutable operator-authored policy revisions and the active in-memory authorization catalog.</summary>
public sealed class ExecutionPolicyService(ExecutionPolicyCatalog catalog, IExecutionPolicyStore store,
    IEnumerable<ExecutionPolicy> seeded, TimeProvider clock)
{
    private readonly ExecutionPolicy[] seeded = seeded.ToArray();
    private ExecutionPolicyRevision[] revisions = [];

    public async Task InitializeAsync(CancellationToken ct)
    {
        await store.InitializeAsync(ct);
        var existing = await store.ListAsync(ct);
        foreach (var policy in seeded)
        {
            var revision = existing.SingleOrDefault(item => item.Name == policy.Name && item.Version == policy.Version);
            if (revision is null) await store.CreateAsync(policy, true, clock.GetUtcNow(), ct);
            else if (revision.Fingerprint != policy.Fingerprint()) throw new ExecutionPolicyConflictException();
        }
        await RefreshAsync(ct);
    }

    public IReadOnlyList<object> DescribeActive() => catalog.Snapshot()
        .Select(policy => revisions.Single(item => item.Name == policy.Name && item.Version == policy.Version))
        .Select(ExecutionPolicyCatalog.Describe).ToArray();

    public IReadOnlyList<object> Describe() => revisions.Select(ExecutionPolicyCatalog.Describe).ToArray();

    public ExecutionPolicyRevision? Get(string name, string version)
        => revisions.SingleOrDefault(item => item.Name == name && item.Version == version);

    public async Task<ExecutionPolicyRevision> CreateAsync(ExecutionPolicy policy, bool activate, CancellationToken ct)
    {
        var revision = await store.CreateAsync(policy, activate, clock.GetUtcNow(), ct);
        await RefreshAsync(ct);
        return revision;
    }

    public async Task<ExecutionPolicyRevision> SetStatusAsync(string name, string version,
        ExecutionPolicyStatus status, CancellationToken ct)
    {
        if (status == ExecutionPolicyStatus.Draft) throw new ArgumentException("Use policy creation to create a Draft revision");
        var revision = await store.SetStatusAsync(name, version, status, clock.GetUtcNow(), ct);
        await RefreshAsync(ct);
        return revision;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        revisions = (await store.ListAsync(ct)).ToArray();
        catalog.Replace(revisions.Where(item => item.Status == ExecutionPolicyStatus.Active).Select(item => item.Policy));
    }
}
