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
}
