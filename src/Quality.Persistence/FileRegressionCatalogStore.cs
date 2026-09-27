using System.Text.Json;
using System.Text.RegularExpressions;
using Quality.Domain;
using Quality.Orchestrator;

namespace Quality.Persistence;

/// <summary>Atomically written single-host regression catalog. Use a shared database store for multi-host writers.</summary>
public sealed class FileRegressionCatalogStore(string directory) : IRegressionCatalogStore
{
    private static readonly Regex IdPattern = new("^SUITE-[a-f0-9]{24}$", RegexOptions.CultureInvariant);
    private readonly string root = Path.GetFullPath(directory);

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        using var gate = await LockAsync(ct);
        foreach (var path in Directory.EnumerateFiles(root, "SUITE-*.json"))
            _ = await ReadPathAsync(path, ct) ?? throw new InvalidOperationException("Regression catalog contains an empty suite");
    }

    public async Task<IReadOnlyList<RegressionSuite>> ListAsync(CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var suites = new List<RegressionSuite>();
        foreach (var path in Directory.EnumerateFiles(root, "SUITE-*.json"))
            if (await ReadPathAsync(path, ct) is { } suite) suites.Add(suite);
        return suites.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<RegressionSuite?> GetAsync(string suiteId, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var path = PathFor(suiteId);
        return File.Exists(path) ? await ReadPathAsync(path, ct) : null;
    }

    public async Task<RegressionSuite> CreateAsync(RegressionSuite suite, CancellationToken ct)
    {
        ValidateSuite(suite);
        using var gate = await LockAsync(ct);
        var path = PathFor(suite.Id);
        if (File.Exists(path))
        {
            var existing = await ReadPathAsync(path, ct) ?? throw new InvalidOperationException("Regression suite is invalid");
            if (JsonSerializer.Serialize(existing, ContractJson.Options) != JsonSerializer.Serialize(suite, ContractJson.Options))
                throw new InvalidOperationException("Regression suite id already exists with different content");
            return existing;
        }
        await WriteAsync(suite, ct);
        return suite;
    }

    public async Task<RegressionSuite> AppendAsync(RegressionVersion version, CancellationToken ct)
    {
        using var gate = await LockAsync(ct);
        var path = PathFor(version.SuiteId);
        var suite = File.Exists(path) ? await ReadPathAsync(path, ct) : null;
        if (suite is null) throw new ArgumentException("Regression suite was not found");
        var updated = RegressionCatalogService.Append(suite, version);
        if (!ReferenceEquals(updated, suite)) await WriteAsync(updated, ct);
        return updated;
    }

    private string PathFor(string id)
    {
        if (!IdPattern.IsMatch(id)) throw new ArgumentException("Invalid regression suite id");
        return Path.Combine(root, id + ".json");
    }

    private static void ValidateSuite(RegressionSuite suite)
    {
        var expected = RegressionCatalogService.CreateSuite(suite.Name);
        if (suite.Id != expected.Id) throw new ArgumentException("Regression suite id does not match its name");
        if (suite.Versions.Length != 0 || suite.ActiveVersionId is not null)
            throw new ArgumentException("Create a regression suite before appending versions");
    }

    private static async Task<RegressionSuite?> ReadPathAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RegressionSuite>(stream, ContractJson.Options, ct);
    }

    private async Task WriteAsync(RegressionSuite suite, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var path = PathFor(suite.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, suite, ContractJson.Options, ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
