using Quality.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quality.Orchestrator;

/// <summary>
/// Explicit operator-owned boundary for autonomous non-production execution.
/// Policies are configuration, never model output or browser input.
/// </summary>
public sealed record ExecutionPolicy(
    string Name,
    string Version,
    string[] AllowedOrigins,
    string[] AllowedActions,
    int MaxTimeoutSeconds = 60,
    bool NonProduction = false,
    bool AutoApprove = false,
    bool AutoLaunch = false,
    int CanaryMaxAutoLaunches = 0)
{
    public void ValidateManifest(ExecutionManifest manifest)
    {
        if (!NonProduction) throw new ArgumentException("Autonomous execution requires a policy explicitly marked non-production");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || string.IsNullOrWhiteSpace(Version) || Version.Length > 100)
            throw new ArgumentException("Policy name and version are required");
        if (!AutoApprove) throw new ArgumentException("Policy does not allow autonomous approval");
        var origin = new Uri(manifest.Target, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
        if (!AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Contains(origin, StringComparer.Ordinal))
            throw new ArgumentException("Target is not allowed by the autonomous policy");
        if (manifest.TimeoutSeconds > MaxTimeoutSeconds)
            throw new ArgumentException("Manifest timeout exceeds the autonomous policy");
        if (AllowedActions.Length == 0 || manifest.Tests.SelectMany(test => test.Steps)
            .Any(step => !AllowedActions.Contains(step.Action, StringComparer.Ordinal)))
            throw new ArgumentException("Manifest contains an action not allowed by the autonomous policy");
    }

    public string ReviewerIdentity() => $"autonomous-policy:{Name}@{Version}";

    public string Fingerprint()
    {
        var canonical = string.Join("\n", Name, Version, NonProduction, AutoApprove, AutoLaunch, CanaryMaxAutoLaunches, MaxTimeoutSeconds,
            string.Join(";", AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Order(StringComparer.Ordinal)),
            string.Join(";", AllowedActions.Order(StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class ExecutionPolicyCatalog(IEnumerable<ExecutionPolicy>? policies = null)
{
    private readonly Dictionary<string, ExecutionPolicy> items = (policies ?? [])
        .ToDictionary(policy => policy.Name, StringComparer.Ordinal);
    private readonly Lock gate = new();

    public ExecutionPolicy Required(string name)
    {
        lock (gate)
            return !string.IsNullOrWhiteSpace(name) && items.TryGetValue(name, out var policy)
                ? policy : throw new ArgumentException("Autonomous execution policy was not found");
    }

    public void Upsert(ExecutionPolicy policy)
    {
        ValidatePolicy(policy);
        lock (gate) items[policy.Name] = policy;
    }

    public IReadOnlyList<ExecutionPolicy> Snapshot()
    {
        lock (gate) return items.Values.OrderBy(policy => policy.Name, StringComparer.Ordinal).ToArray();
    }

    public static void ValidatePolicy(ExecutionPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name) || policy.Name.Length > 100 ||
            string.IsNullOrWhiteSpace(policy.Version) || policy.Version.Length > 100)
            throw new ArgumentException("Policy name and version are required and must be at most 100 characters");
        if (!System.Text.RegularExpressions.Regex.IsMatch(policy.Name, "^[a-z0-9][a-z0-9-]{0,99}$"))
            throw new ArgumentException("Policy name must use lowercase letters, digits, and hyphens");
        if (policy.MaxTimeoutSeconds is < 1 or > 600) throw new ArgumentException("Policy timeout must be between 1 and 600 seconds");
        if (policy.CanaryMaxAutoLaunches is < 0 or > 100) throw new ArgumentException("Canary launch budget must be between 0 and 100");
        if (!policy.NonProduction && (policy.AutoApprove || policy.AutoLaunch))
            throw new ArgumentException("Only explicitly non-production policies may auto-approve or auto-launch");
        if (policy.AutoLaunch && !policy.AutoApprove)
            throw new ArgumentException("Auto-launch requires auto-approval");
        if (policy.AllowedOrigins.Length == 0) throw new ArgumentException("At least one allowed origin is required");
        foreach (var origin in policy.AllowedOrigins) ExecutionManifest.ValidateOrigin(origin);
        var supported = new HashSet<string>(["goto", "click", "fill", "expectText", "expectVisible", "expectUrl"], StringComparer.Ordinal);
        if (policy.AllowedActions.Length == 0 || policy.AllowedActions.Any(action => !supported.Contains(action)))
            throw new ArgumentException("Policy actions must be supported browser actions");
    }

    public IReadOnlyList<object> Describe() => Snapshot()
        .Select(policy => (object)new
        {
            policy.Name,
            policy.Version,
            policy.NonProduction,
            policy.AutoApprove,
            policy.AutoLaunch,
            policy.CanaryMaxAutoLaunches,
            policy.MaxTimeoutSeconds,
            AllowedOrigins = policy.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Order().ToArray(),
            AllowedActions = policy.AllowedActions.Order().ToArray(),
            Fingerprint = policy.Fingerprint()
        }).ToArray();
}

/// <summary>Small durable, atomically written policy catalog shared by API restarts.</summary>
public sealed class FileExecutionPolicyStore(string path)
{
    public async Task<IReadOnlyList<ExecutionPolicy>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ExecutionPolicy[]>(stream, cancellationToken: ct) ?? [];
    }

    public async Task SaveAsync(IReadOnlyList<ExecutionPolicy> policies, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Policy store path must include a directory");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, policies, cancellationToken: ct);
        File.Move(temporary, path, true);
    }
}
