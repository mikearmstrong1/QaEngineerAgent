using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

/// <summary>Single-host durable schedule store. PostgreSQL is required for concurrent hosts.</summary>
public sealed class FileAutomationScheduleStore(string directory) : IAutomationScheduleStore
{
    private readonly string root = Path.GetFullPath(directory);

    public Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public async Task<AutomationSchedule> CreateAsync(AutomationSchedule schedule, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(path), ct) is { } item
                && string.Equals(item.Name, schedule.Name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An automation schedule with that name already exists");
        await WriteAsync(schedule, ct);
        return schedule;
    }

    public async Task<AutomationSchedule?> GetAsync(string id, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        return await ReadAsync(id, ct);
    }

    public async Task<IReadOnlyList<AutomationSchedule>> ListAsync(CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var items = new List<AutomationSchedule>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(path), ct) is { } item) items.Add(item);
        return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<AutomationSchedule> SetEnabledAsync(string id, long expectedRevision, bool enabled,
        DateTimeOffset now, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(id, ct) ?? throw new ArgumentException("Automation schedule not found");
        if (current.Revision != expectedRevision) throw new AutomationScheduleConflictException();
        var saved = current with { Enabled = enabled, UpdatedAt = now, Revision = current.Revision + 1,
            LeaseToken = null, LeaseUntil = null, ClaimedOccurrenceAt = null };
        await WriteAsync(saved, ct);
        return saved;
    }

    public async Task<AutomationSchedule?> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var items = new List<AutomationSchedule>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(path), ct) is { } item) items.Add(item);
        var current = items.Where(item => item.Enabled && item.NextOccurrenceAt <= now
                && (item.LeaseUntil is null || item.LeaseUntil <= now))
            .OrderBy(item => item.NextOccurrenceAt).ThenBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
        if (current is null) return null;
        var claimed = current with { LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease,
            ClaimedOccurrenceAt = current.NextOccurrenceAt, UpdatedAt = now, Revision = current.Revision + 1 };
        await WriteAsync(claimed, ct);
        return claimed;
    }

    public async Task<AutomationSchedule> CompleteClaimAsync(AutomationSchedule schedule, long expectedRevision,
        string leaseToken, string workflowId, DateTimeOffset now, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(schedule.Id, ct);
        if (current is null || current.Revision != expectedRevision || current.LeaseToken != leaseToken
            || current.LeaseUntil is null || current.LeaseUntil <= now || current.ClaimedOccurrenceAt is null)
            throw new AutomationScheduleConflictException();
        var saved = current with
        {
            LastOccurrenceAt = current.ClaimedOccurrenceAt,
            LastWorkflowId = workflowId,
            NextOccurrenceAt = AutomationScheduleService.NextAfter(current.ClaimedOccurrenceAt.Value,
                current.IntervalSeconds, now),
            LeaseToken = null,
            LeaseUntil = null,
            ClaimedOccurrenceAt = null,
            UpdatedAt = now,
            Revision = current.Revision + 1
        };
        await WriteAsync(saved, ct);
        return saved;
    }

    public async Task ReleaseClaimAsync(string id, long expectedRevision, string leaseToken, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(id, ct);
        if (current is null || current.Revision != expectedRevision || current.LeaseToken != leaseToken) return;
        await WriteAsync(current with { LeaseToken = null, LeaseUntil = null, ClaimedOccurrenceAt = null,
            Revision = current.Revision + 1 }, ct);
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid automation schedule id");
        return Path.Combine(root, id + ".json");
    }

    private async Task<AutomationSchedule?> ReadAsync(string id, CancellationToken ct)
    {
        var path = PathFor(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<AutomationSchedule>(await File.ReadAllTextAsync(path, ct), ContractJson.Options)
            : null;
    }

    private async Task WriteAsync(AutomationSchedule schedule, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var path = PathFor(schedule.Id);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(schedule, ContractJson.Options), ct);
        File.Move(temporary, path, true);
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, ct); }
        }
    }
}
