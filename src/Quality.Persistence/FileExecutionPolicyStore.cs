using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

/// <summary>Small atomically written policy-revision store for local development.</summary>
public sealed class FileExecutionPolicyStore(string path) : IExecutionPolicyStore
{
    private readonly string file = Path.GetFullPath(path);

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var revisions = await LoadUnlockedAsync(ct);
        if (!File.Exists(file) || await IsLegacyAsync(ct)) await SaveUnlockedAsync(revisions, ct);
    }

    public async Task<IReadOnlyList<ExecutionPolicyRevision>> ListAsync(CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        return (await LoadUnlockedAsync(ct)).OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenByDescending(item => item.CreatedAt).ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();
    }

    public async Task<ExecutionPolicyRevision?> GetAsync(string name, string version, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        return (await LoadUnlockedAsync(ct)).SingleOrDefault(item => item.Name == name && item.Version == version);
    }

    public async Task<ExecutionPolicyRevision> CreateAsync(ExecutionPolicy policy, bool activate, DateTimeOffset now, CancellationToken ct)
    {
        ExecutionPolicyCatalog.ValidatePolicy(policy);
        using var gate = await LockAsync(ct);
        var items = (await LoadUnlockedAsync(ct)).ToList();
        var existing = items.SingleOrDefault(item => item.Name == policy.Name && item.Version == policy.Version);
        if (existing is not null)
        {
            if (existing.Fingerprint != policy.Fingerprint()) throw new ExecutionPolicyConflictException();
            if (!activate || existing.Status == ExecutionPolicyStatus.Active) return existing;
            return await SetStatusUnlockedAsync(items, existing, ExecutionPolicyStatus.Active, now, ct);
        }
        var revision = new ExecutionPolicyRevision(policy,
            activate ? ExecutionPolicyStatus.Active : ExecutionPolicyStatus.Draft, now, now);
        if (activate) RetireActive(items, policy.Name, now);
        items.Add(revision);
        await SaveUnlockedAsync(items, ct);
        return revision;
    }

    public async Task<ExecutionPolicyRevision> SetStatusAsync(string name, string version, ExecutionPolicyStatus status,
        DateTimeOffset now, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var items = (await LoadUnlockedAsync(ct)).ToList();
        var current = items.SingleOrDefault(item => item.Name == name && item.Version == version)
            ?? throw new ArgumentException("Execution policy revision was not found");
        return await SetStatusUnlockedAsync(items, current, status, now, ct);
    }

    private async Task<ExecutionPolicyRevision> SetStatusUnlockedAsync(List<ExecutionPolicyRevision> items,
        ExecutionPolicyRevision current, ExecutionPolicyStatus status, DateTimeOffset now, CancellationToken ct)
    {
        ValidateTransition(current.Status, status);
        if (current.Status == status) return current;
        if (status == ExecutionPolicyStatus.Active) RetireActive(items, current.Name, now);
        var updated = current with { Status = status, UpdatedAt = now };
        items[items.FindIndex(item => item.Name == current.Name && item.Version == current.Version)] = updated;
        await SaveUnlockedAsync(items, ct);
        return updated;
    }

    private static void RetireActive(List<ExecutionPolicyRevision> items, string name, DateTimeOffset now)
    {
        for (var index = 0; index < items.Count; index++)
            if (items[index].Name == name && items[index].Status == ExecutionPolicyStatus.Active)
                items[index] = items[index] with { Status = ExecutionPolicyStatus.Retired, UpdatedAt = now };
    }

    internal static void ValidateTransition(ExecutionPolicyStatus current, ExecutionPolicyStatus next)
    {
        if (next == ExecutionPolicyStatus.Draft && current != ExecutionPolicyStatus.Draft)
            throw new ArgumentException("A policy revision cannot return to Draft");
        if (current == ExecutionPolicyStatus.Retired && next != ExecutionPolicyStatus.Retired)
            throw new ArgumentException("A retired policy revision cannot be reactivated");
    }

    private async Task<List<ExecutionPolicyRevision>> LoadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        await using var stream = File.OpenRead(file);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Execution policy store is invalid");
        var elements = document.RootElement.EnumerateArray().ToArray();
        if (elements.Length == 0) return [];
        if (elements[0].TryGetProperty("policy", out _))
            return JsonSerializer.Deserialize<List<ExecutionPolicyRevision>>(document.RootElement.GetRawText(), ContractJson.Options) ?? [];
        var now = DateTimeOffset.UtcNow;
        var legacy = JsonSerializer.Deserialize<List<ExecutionPolicy>>(document.RootElement.GetRawText(), ContractJson.Options) ?? [];
        return legacy.Select(policy => new ExecutionPolicyRevision(policy, ExecutionPolicyStatus.Active, now, now)).ToList();
    }

    private async Task<bool> IsLegacyAsync(CancellationToken ct)
    {
        if (!File.Exists(file)) return false;
        await using var stream = File.OpenRead(file);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var first = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault() : default;
        return first.ValueKind == JsonValueKind.Object && !first.TryGetProperty("policy", out _);
    }

    private async Task SaveUnlockedAsync(IEnumerable<ExecutionPolicyRevision> revisions, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(file) ?? throw new ArgumentException("Policy store path must include a directory");
        Directory.CreateDirectory(directory);
        var temporary = file + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, revisions, ContractJson.Options, ct);
        File.Move(temporary, file, true);
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, ct); }
        }
    }
}
