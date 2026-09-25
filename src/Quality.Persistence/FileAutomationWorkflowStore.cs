using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

/// <summary>Single-host durable workflow fallback. PostgreSQL is required for multi-host workers.</summary>
public sealed class FileAutomationWorkflowStore(string directory) : IAutomationWorkflowStore
{
    private readonly string root = Path.GetFullPath(directory);

    public async Task<AutomationWorkflow> CreateOrGetAsync(AutomationWorkflow workflow, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var existing = await ReadAsync(Path.GetFileNameWithoutExtension(path), ct);
            if (existing?.IdempotencyKeyHash != workflow.IdempotencyKeyHash) continue;
            if (existing.InputHash != workflow.InputHash) throw new IdempotencyConflictException();
            return existing;
        }
        await WriteAsync(workflow, ct);
        return workflow;
    }

    public async Task<AutomationWorkflow?> GetAsync(string id, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        return await ReadAsync(id, ct);
    }

    public async Task<IReadOnlyList<AutomationWorkflow>> ListByJobAsync(string jobId, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var items = new List<AutomationWorkflow>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(path), ct) is { } item && item.JobId == jobId)
                items.Add(item);
        return items.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<AutomationWorkflow?> ClaimAsync(TimeSpan lease, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var candidates = new List<AutomationWorkflow>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(path), ct) is { } item) candidates.Add(item);
        var current = candidates.Where(item => !item.IsTerminal && item.Status != AutomationWorkflowStatus.AwaitingReview
                && (item.LeaseUntil is null || item.LeaseUntil <= now) && (item.NextAttemptAt is null || item.NextAttemptAt <= now))
            .OrderBy(item => item.CreatedAt).FirstOrDefault();
        if (current is null) return null;
        var claimed = current with { LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease,
            StageAttempts = current.StageAttempts + 1, UpdatedAt = now, Revision = current.Revision + 1 };
        await WriteAsync(claimed, ct);
        return claimed;
    }

    public async Task<AutomationWorkflow> SaveAsync(AutomationWorkflow workflow, long expectedRevision,
        string leaseToken, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(workflow.Id, ct);
        if (current is null || current.Revision != expectedRevision || current.LeaseToken != leaseToken
            || current.LeaseUntil is null || current.LeaseUntil <= DateTimeOffset.UtcNow)
            throw new AutomationWorkflowConflictException();
        var saved = workflow with { Revision = expectedRevision + 1, LeaseToken = null, LeaseUntil = null };
        await WriteAsync(saved, ct);
        return saved;
    }

    public async Task<AutomationWorkflow> SaveReviewAsync(AutomationWorkflow workflow, long expectedRevision, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(workflow.Id, ct);
        if (current is null || current.Revision != expectedRevision || current.Status != AutomationWorkflowStatus.AwaitingReview)
            throw new AutomationWorkflowConflictException();
        var saved = workflow with { Revision = expectedRevision + 1, LeaseToken = null, LeaseUntil = null };
        await WriteAsync(saved, ct);
        return saved;
    }

    public async Task<AutomationWorkflow?> CancelAsync(string id, DateTimeOffset now, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(id, ct);
        if (current is null || current.IsTerminal) return current;
        var cancelled = current with { Status = AutomationWorkflowStatus.Cancelled, UpdatedAt = now,
            Error = "automation_cancelled", LeaseToken = null, LeaseUntil = null, NextAttemptAt = null,
            Revision = current.Revision + 1, Checkpoints = [.. current.Checkpoints,
                new(AutomationWorkflowStatus.Cancelled, now, $"cancel:{current.Revision + 1}")] };
        await WriteAsync(cancelled, ct);
        return cancelled;
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid automation workflow id");
        return Path.Combine(root, id + ".json");
    }
    private async Task<AutomationWorkflow?> ReadAsync(string id, CancellationToken ct)
    {
        var path = PathFor(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<AutomationWorkflow>(await File.ReadAllTextAsync(path, ct), ContractJson.Options)
            : null;
    }
    private async Task WriteAsync(AutomationWorkflow workflow, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var path = PathFor(workflow.Id);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(workflow, ContractJson.Options), ct);
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
