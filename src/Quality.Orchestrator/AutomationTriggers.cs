using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Quality.Orchestrator;

public sealed record SignedAutomationTrigger(
    string TriggerId,
    string JobId,
    string Target,
    string PolicyName,
    long IssuedAtUnixSeconds,
    string Signature);

public sealed class AutomationTriggerOptions
{
    public string SigningSecret { get; init; } = "";
    public int MaximumAgeSeconds { get; init; } = 300;
}

/// <summary>
/// Verifies deployment/source trigger envelopes before they are allowed to create an idempotent workflow.
/// The secret remains a server-side configuration value and is never included in the returned envelope.
/// </summary>
public sealed class AutomationTriggerVerifier(AutomationTriggerOptions options, TimeProvider clock)
{
    public string Verify(SignedAutomationTrigger trigger)
    {
        if (options.SigningSecret.Length < 32)
            throw new InvalidOperationException("Automation trigger signing is not configured");
        if (options.MaximumAgeSeconds is < 30 or > 3600)
            throw new InvalidOperationException("Automation trigger maximum age must be between 30 and 3600 seconds");
        if (string.IsNullOrWhiteSpace(trigger.TriggerId) || trigger.TriggerId.Length > 200
            || trigger.TriggerId.Any(char.IsControl))
            throw new ArgumentException("Trigger id must contain 1-200 non-control characters");
        if (trigger.Signature.Length != 64 || trigger.Signature.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Trigger signature is invalid");

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(trigger.IssuedAtUnixSeconds);
        var age = clock.GetUtcNow() - issuedAt;
        if (age < TimeSpan.FromSeconds(-30) || age > TimeSpan.FromSeconds(options.MaximumAgeSeconds))
            throw new ArgumentException("Trigger is stale or issued in the future");

        var expected = Sign(options.SigningSecret, trigger.TriggerId, trigger.JobId, trigger.Target,
            trigger.PolicyName, trigger.IssuedAtUnixSeconds);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(trigger.Signature.ToLowerInvariant())))
            throw new ArgumentException("Trigger signature is invalid");
        return trigger.TriggerId;
    }

    public static string Sign(string secret, string triggerId, string jobId, string target,
        string policyName, long issuedAtUnixSeconds)
    {
        var payload = string.Join("\n", triggerId, jobId, target, policyName,
            issuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
