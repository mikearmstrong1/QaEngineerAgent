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
    int CanaryMaxAutoLaunches = 0,
    string Environment = "default",
    int MaxConcurrentAutoLaunches = 1,
    int AutoLaunchWindowSeconds = 3600,
    int MaxAutoLaunchesPerWindow = 1,
    MappingConfidence MinimumMappingConfidence = MappingConfidence.High,
    MutationGovernance? MutationGovernance = null,
    int MaxInfrastructureReruns = 0,
    int MaxFlakyTestReruns = 0,
    string[]? FlakyTestIds = null)
{
    public void ValidateManifest(ExecutionManifest manifest, bool requireAutoApproval = true)
    {
        if (!NonProduction) throw new ArgumentException("Autonomous execution requires a policy explicitly marked non-production");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || string.IsNullOrWhiteSpace(Version) || Version.Length > 100)
            throw new ArgumentException("Policy name and version are required");
        if (requireAutoApproval && !AutoApprove) throw new ArgumentException("Policy does not allow autonomous approval");
        var origin = new Uri(manifest.Target, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
        if (!AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Contains(origin, StringComparer.Ordinal))
            throw new ArgumentException("Target is not allowed by the autonomous policy");
        if (manifest.TimeoutSeconds > MaxTimeoutSeconds)
            throw new ArgumentException("Manifest timeout exceeds the autonomous policy");
        if (AllowedActions.Length == 0 || manifest.Tests.SelectMany(test => test.Steps)
            .Any(step => !AllowedActions.Contains(step.Action, StringComparer.Ordinal)))
            throw new ArgumentException("Manifest contains an action not allowed by the autonomous policy");
    }

    public void ValidatePreparation(ManifestPreparationResult preparation)
    {
        if (!preparation.Complete || preparation.Gaps.Length != 0)
            throw new ArgumentException("Prepared manifest has unmatched or ambiguous planned coverage");
        if ((int)preparation.OverallConfidence < (int)MinimumMappingConfidence)
            throw new ArgumentException("Prepared manifest does not meet the policy confidence threshold");
        if (preparation.HasMutations)
        {
            if (MutationGovernance is null)
                throw new ArgumentException("Autonomous mutation requires declared test identity, data, preconditions, and cleanup");
            ExecutionPolicyCatalog.ValidateMutationGovernance(MutationGovernance);
        }
    }

    public string ReviewerIdentity() => $"autonomous-policy:{Name}@{Version}";

    public string CanonicalJson()
    {
        // Omitting the new default preserves fingerprints for policy revisions created before coverage gating existed.
        var value = new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["version"] = Version,
            ["allowedOrigins"] = AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Order(StringComparer.Ordinal).ToArray(),
            ["allowedActions"] = AllowedActions.Order(StringComparer.Ordinal).ToArray(),
            ["maxTimeoutSeconds"] = MaxTimeoutSeconds,
            ["nonProduction"] = NonProduction,
            ["autoApprove"] = AutoApprove,
            ["autoLaunch"] = AutoLaunch,
            ["canaryMaxAutoLaunches"] = CanaryMaxAutoLaunches,
            ["environment"] = Environment,
            ["maxConcurrentAutoLaunches"] = MaxConcurrentAutoLaunches,
            ["autoLaunchWindowSeconds"] = AutoLaunchWindowSeconds,
            ["maxAutoLaunchesPerWindow"] = MaxAutoLaunchesPerWindow
        };
        if (MinimumMappingConfidence != MappingConfidence.High)
            value["minimumMappingConfidence"] = MinimumMappingConfidence;
        if (MutationGovernance is not null)
            value["mutationGovernance"] = MutationGovernance;
        if (MaxInfrastructureReruns != 0) value["maxInfrastructureReruns"] = MaxInfrastructureReruns;
        if (MaxFlakyTestReruns != 0) value["maxFlakyTestReruns"] = MaxFlakyTestReruns;
        if (FlakyTestIds is { Length: > 0 }) value["flakyTestIds"] = FlakyTestIds.Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(value, ContractJson.Options);
    }

    public string Fingerprint()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson()))).ToLowerInvariant();
}

/// <summary>
/// Secret-free references to operator-managed mutation controls. The referenced data and cleanup
/// implementations remain behind server-side provider boundaries and are never embedded in policy JSON.
/// </summary>
public sealed record MutationGovernance(
    string TestIdentityReference,
    string TestDataProfileReference,
    string[] PreconditionReferences,
    string[] CleanupActionReferences,
    string CleanupVerificationReference);

public enum ExecutionPolicyStatus { Draft, Active, Disabled, Retired }

public sealed record ExecutionPolicyRevision(ExecutionPolicy Policy, ExecutionPolicyStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string Name => Policy.Name;
    public string Version => Policy.Version;
    public string Fingerprint => Policy.Fingerprint();
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

    public void Replace(IEnumerable<ExecutionPolicy> policies)
    {
        var replacement = policies.ToArray();
        foreach (var policy in replacement) ValidatePolicy(policy);
        lock (gate)
        {
            items.Clear();
            foreach (var policy in replacement) items.Add(policy.Name, policy);
        }
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
        if (!System.Text.RegularExpressions.Regex.IsMatch(policy.Version, "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$"))
            throw new ArgumentException("Policy version must use letters, digits, periods, underscores, and hyphens");
        if (string.IsNullOrWhiteSpace(policy.Environment) || !System.Text.RegularExpressions.Regex.IsMatch(policy.Environment, "^[a-z0-9][a-z0-9-]{0,99}$"))
            throw new ArgumentException("Policy environment must use lowercase letters, digits, and hyphens");
        if (policy.MaxTimeoutSeconds is < 1 or > 600) throw new ArgumentException("Policy timeout must be between 1 and 600 seconds");
        if (policy.CanaryMaxAutoLaunches is < 0 or > 100) throw new ArgumentException("Canary launch budget must be between 0 and 100");
        if (policy.MaxConcurrentAutoLaunches is < 1 or > 100) throw new ArgumentException("Concurrent launch budget must be between 1 and 100");
        if (policy.AutoLaunchWindowSeconds is < 60 or > 86400) throw new ArgumentException("Launch budget window must be between 60 and 86400 seconds");
        if (policy.MaxAutoLaunchesPerWindow is < 1 or > 1000) throw new ArgumentException("Window launch budget must be between 1 and 1000");
        if (!Enum.IsDefined(policy.MinimumMappingConfidence) || policy.MinimumMappingConfidence == MappingConfidence.None)
            throw new ArgumentException("Policy minimum mapping confidence must be Low, Medium, or High");
        if (policy.MutationGovernance is { } governance) ValidateMutationGovernance(governance);
        if (policy.MaxInfrastructureReruns is < 0 or > 5 || policy.MaxFlakyTestReruns is < 0 or > 5)
            throw new ArgumentException("Automatic rerun budgets must be between 0 and 5");
        var flakyIds = policy.FlakyTestIds ?? [];
        foreach (var id in flakyIds) ValidateTestId(id);
        if (flakyIds.Distinct(StringComparer.Ordinal).Count() != flakyIds.Length)
            throw new ArgumentException("Declared flaky test IDs must be unique");
        if (policy.MaxFlakyTestReruns > 0 && flakyIds.Length == 0)
            throw new ArgumentException("Flaky-test reruns require declared flaky test IDs");
        if (policy.MaxFlakyTestReruns == 0 && flakyIds.Length > 0)
            throw new ArgumentException("Declared flaky test IDs require a nonzero flaky-test rerun budget");
        if (!policy.NonProduction && (policy.AutoApprove || policy.AutoLaunch))
            throw new ArgumentException("Only explicitly non-production policies may auto-approve or auto-launch");
        if (policy.AutoLaunch && !policy.AutoApprove)
            throw new ArgumentException("Auto-launch requires auto-approval");
        if (policy.AllowedOrigins is not { Length: > 0 }) throw new ArgumentException("At least one allowed origin is required");
        var origins = policy.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).ToArray();
        if (origins.Distinct(StringComparer.Ordinal).Count() != origins.Length)
            throw new ArgumentException("Policy origins must be unique");
        var supported = new HashSet<string>(["goto", "click", "fill", "expectText", "expectVisible", "expectUrl"], StringComparer.Ordinal);
        if (policy.AllowedActions is not { Length: > 0 } || policy.AllowedActions.Any(action => !supported.Contains(action)))
            throw new ArgumentException("Policy actions must be supported browser actions");
        if (policy.AllowedActions.Distinct(StringComparer.Ordinal).Count() != policy.AllowedActions.Length)
            throw new ArgumentException("Policy actions must be unique");
    }

    public static object Describe(ExecutionPolicyRevision revision) => new
        {
            revision.Policy.Name,
            revision.Policy.Version,
            revision.Status,
            revision.CreatedAt,
            revision.UpdatedAt,
            revision.Policy.NonProduction,
            revision.Policy.AutoApprove,
            revision.Policy.AutoLaunch,
            revision.Policy.CanaryMaxAutoLaunches,
            revision.Policy.Environment,
            revision.Policy.MaxConcurrentAutoLaunches,
            revision.Policy.AutoLaunchWindowSeconds,
            revision.Policy.MaxAutoLaunchesPerWindow,
            revision.Policy.MinimumMappingConfidence,
            revision.Policy.MutationGovernance,
            revision.Policy.MaxInfrastructureReruns,
            revision.Policy.MaxFlakyTestReruns,
            FlakyTestIds = revision.Policy.FlakyTestIds?.Order(StringComparer.Ordinal).ToArray() ?? [],
            revision.Policy.MaxTimeoutSeconds,
            AllowedOrigins = revision.Policy.AllowedOrigins.Select(ExecutionManifest.ValidateOrigin).Order().ToArray(),
            AllowedActions = revision.Policy.AllowedActions.Order().ToArray(),
            revision.Fingerprint
        };

    internal static void ValidateMutationGovernance(MutationGovernance governance)
    {
        ValidateReference(governance.TestIdentityReference, "Test identity");
        ValidateReference(governance.TestDataProfileReference, "Test data profile");
        ValidateReferences(governance.PreconditionReferences, "Precondition");
        ValidateReferences(governance.CleanupActionReferences, "Cleanup action");
        ValidateReference(governance.CleanupVerificationReference, "Cleanup verification");
    }

    private static void ValidateReferences(string[]? references, string label)
    {
        if (references is not { Length: > 0 }) throw new ArgumentException($"{label} references require at least one named reference");
        foreach (var reference in references) ValidateReference(reference, label);
        if (references.Distinct(StringComparer.Ordinal).Count() != references.Length)
            throw new ArgumentException($"{label} references must be unique");
    }

    private static void ValidateReference(string? reference, string label)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            !System.Text.RegularExpressions.Regex.IsMatch(reference, "^[a-z0-9][a-z0-9._-]{0,99}$"))
            throw new ArgumentException($"{label} reference must be a lowercase named reference without secret values");
    }

    private static void ValidateTestId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$"))
            throw new ArgumentException("Declared flaky test IDs must be secret-free identifiers");
    }

    public IReadOnlyList<object> Describe() => Snapshot()
        .Select(policy => Describe(new(policy, ExecutionPolicyStatus.Active, DateTimeOffset.MinValue, DateTimeOffset.MinValue)))
        .ToArray();
}
