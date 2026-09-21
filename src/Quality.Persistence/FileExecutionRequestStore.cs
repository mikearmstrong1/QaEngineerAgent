using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

public sealed class FileExecutionRequestStore(string directory) : IExecutionRequestStore
{
    private readonly string root = Path.GetFullPath(directory);

    public async Task CreateAsync(ExecutionRequest request, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        if (File.Exists(PathFor(request.Id))) throw new ExecutionRequestConflictException();
        await WriteAsync(request, ct);
    }

    public async Task<ExecutionRequest?> GetAsync(string id, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        return await ReadAsync(id, ct);
    }

    public async Task<IReadOnlyList<ExecutionRequest>> ListByJobAsync(string jobId, CancellationToken ct)
    {
        ValidateId(jobId, "job");
        using var gate = await LockAsync(ct);
        var items = new List<ExecutionRequest>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(file), ct) is { } item && item.JobId == jobId)
                items.Add(item);
        return items.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<ExecutionRequest> SaveAsync(ExecutionRequest request, long expectedRevision, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(request.Id, ct);
        if (current is null || current.Revision != expectedRevision) throw new ExecutionRequestConflictException();
        var saved = request with { Revision = expectedRevision + 1 };
        await WriteAsync(saved, ct);
        return saved;
    }

    public async Task<ExecutionRequest?> ClaimAsync(TimeSpan lease, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var items = new List<ExecutionRequest>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(file), ct) is { } item) items.Add(item);
        var current = items.Where(x => x.Status == ExecutionRequestStatus.Queued ||
                x.Status == ExecutionRequestStatus.Running && x.LeaseUntil <= now)
            .OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (current is null) return null;
        ExecutionRequest claimed;
        if (current.Status == ExecutionRequestStatus.Running)
            claimed = current with { Status = ExecutionRequestStatus.InfrastructureFailed, Error = "execution_worker_interrupted",
                UpdatedAt = now, LeaseToken = null, LeaseUntil = null, Revision = current.Revision + 1 };
        else
            claimed = current with { Status = ExecutionRequestStatus.Running, UpdatedAt = now,
                LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease,
                Attempts = current.Attempts + 1, Revision = current.Revision + 1 };
        await WriteAsync(claimed, ct);
        return claimed;
    }

    private string PathFor(string id) { ValidateId(id, "execution request"); return Path.Combine(root, id + ".json"); }
    private static void ValidateId(string id, string kind)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException($"Invalid {kind} id");
    }
    private async Task<ExecutionRequest?> ReadAsync(string id, CancellationToken ct)
    {
        var path = PathFor(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ExecutionRequest>(await File.ReadAllTextAsync(path, ct), ContractJson.Options)
            : null;
    }
    private async Task WriteAsync(ExecutionRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var path = PathFor(request.Id);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(request, ContractJson.Options), ct);
        File.Move(temp, path, true);
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
