using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
namespace Quality.Persistence;

// Small, durable local fallback. Shared local disk only; use PostgreSQL across hosts.
public sealed class FileJobStore(string directory, TimeProvider clock) : IJobStore
{
    private readonly string root = Path.GetFullPath(directory);
    public Task InitializeAsync(CancellationToken ct) { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public async Task CreateAsync(QualityJob job, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        if (File.Exists(JobPath(job.Id))) throw new InvalidOperationException("Job already exists");
        await WriteAsync(job, ct);
    }
    public async Task<QualityJob?> GetAsync(string id, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        return await ReadAsync(id, ct);
    }
    public async Task<QualityJob?> ClaimAsync(string? id, TimeSpan lease, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var now = clock.GetUtcNow();
        var jobs = new List<QualityJob>();
        if (id is not null) { if (await ReadAsync(id, ct) is { } found) jobs.Add(found); }
        else foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            if (await ReadAsync(Path.GetFileNameWithoutExtension(file), ct) is { } found) jobs.Add(found);
        var job = jobs.Where(j => !j.IsTerminal && (j.LeaseUntil is null || j.LeaseUntil <= now))
            .OrderBy(j => j.CreatedAt).FirstOrDefault();
        if (job is null) return null;
        job = job with { LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease, Revision = job.Revision + 1 };
        await WriteAsync(job, ct);
        return job;
    }
    public async Task<QualityJob> SaveAsync(QualityJob job, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var current = await ReadAsync(job.Id, ct);
        if (current is null || current.Revision != job.Revision || current.LeaseToken != job.LeaseToken ||
            current.LeaseToken is null || current.LeaseUntil <= clock.GetUtcNow()) throw new LeaseLostException();
        job = job with { Revision = job.Revision + 1, LeaseToken = job.IsTerminal ? null : job.LeaseToken,
            LeaseUntil = job.IsTerminal ? null : job.LeaseUntil };
        await WriteAsync(job, ct);
        return job;
    }
    private string JobPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid job id");
        return Path.Combine(root, id + ".json");
    }
    private async Task<QualityJob?> ReadAsync(string id, CancellationToken ct)
    {
        var path = JobPath(id);
        return File.Exists(path) ? JsonSerializer.Deserialize<QualityJob>(await File.ReadAllTextAsync(path, ct), ContractJson.Options) : null;
    }
    private async Task WriteAsync(QualityJob job, CancellationToken ct)
    {
        var path = JobPath(job.Id);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(job, ContractJson.Options), ct);
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
