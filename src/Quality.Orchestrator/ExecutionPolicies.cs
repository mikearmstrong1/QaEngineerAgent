using Quality.Domain;
using System.Security.Cryptography;
using System.Text;

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
    bool AutoLaunch = false)
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
        var canonical = string.Join("\n", Name, Version, NonProduction, AutoApprove, AutoLaunch, MaxTimeoutSeconds,
            string.Join(";", AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Order(StringComparer.Ordinal)),
            string.Join(";", AllowedActions.Order(StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class ExecutionPolicyCatalog(IEnumerable<ExecutionPolicy>? policies = null)
{
    private readonly Dictionary<string, ExecutionPolicy> items = (policies ?? [])
        .ToDictionary(policy => policy.Name, StringComparer.Ordinal);

    public ExecutionPolicy Required(string name) => !string.IsNullOrWhiteSpace(name) && items.TryGetValue(name, out var policy)
        ? policy : throw new ArgumentException("Autonomous execution policy was not found");

    public IReadOnlyList<object> Describe() => items.Values.OrderBy(policy => policy.Name, StringComparer.Ordinal)
        .Select(policy => (object)new { policy.Name, policy.Version, policy.NonProduction, policy.AutoApprove, policy.AutoLaunch }).ToArray();
}
