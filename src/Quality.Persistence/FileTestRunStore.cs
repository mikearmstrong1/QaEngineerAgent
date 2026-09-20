using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
namespace Quality.Persistence;

public sealed class FileTestRunStore(string root) : ITestRunStore
{
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid run ID");
        return Path.Combine(Path.GetFullPath(root), id);
    }
    public async Task SaveAsync(TestRun run, CancellationToken ct)
    {
        var directory = DirectoryFor(run.Id);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(run, ContractJson.Options), ct);
            File.Move(temporary, Path.Combine(directory, "run.json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<TestRun?> GetAsync(string id, CancellationToken ct)
    {
        var path = Path.Combine(DirectoryFor(id), "run.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<TestRun>(await File.ReadAllTextAsync(path, ct), ContractJson.Options) : null;
    }
    public async Task<IReadOnlyList<TestRun>> ListByPlanAsync(string testPlanId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testPlanId) || testPlanId.Length > 200 || testPlanId.Any(char.IsControl))
            throw new ArgumentException("Invalid test plan ID");
        var runs = new List<TestRun>();
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) return runs;
        foreach (var directory in Directory.EnumerateDirectories(fullRoot))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            if (await GetAsync(id, ct) is { } run && run.TestPlanId == testPlanId) runs.Add(run);
        }
        return runs.OrderByDescending(run => run.StartedAt).ThenByDescending(run => run.Id).Take(100).ToArray();
    }
}
