using Quality.Orchestrator;
using Xunit;

namespace Quality.Tests;

public sealed class AutomationTriggerTests
{
    private const string Secret = "local-test-secret-with-at-least-32-characters";

    [Fact]
    public void ValidSignatureReturnsStableIdempotencyKey()
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var issued = now.ToUnixTimeSeconds();
        var signature = AutomationTriggerVerifier.Sign(Secret, "deploy-42", new string('a', 32),
            "https://test.example/", "safe", issued);
        var verifier = new AutomationTriggerVerifier(new() { SigningSecret = Secret }, new FixedTimeProvider(now));

        var key = verifier.Verify(new("deploy-42", new string('a', 32), "https://test.example/", "safe", issued, signature));

        Assert.Equal("deploy-42", key);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(31)]
    public void StaleOrFutureTriggerIsRejected(int offsetSeconds)
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var issued = now.AddSeconds(offsetSeconds).ToUnixTimeSeconds();
        var signature = AutomationTriggerVerifier.Sign(Secret, "deploy-42", new string('a', 32),
            "https://test.example/", "safe", issued);
        var verifier = new AutomationTriggerVerifier(new() { SigningSecret = Secret }, new FixedTimeProvider(now));

        Assert.Throws<ArgumentException>(() => verifier.Verify(new("deploy-42", new string('a', 32),
            "https://test.example/", "safe", issued, signature)));
    }

    [Fact]
    public void ChangedPayloadAndMissingServerSecretFailClosed()
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var issued = now.ToUnixTimeSeconds();
        var signature = AutomationTriggerVerifier.Sign(Secret, "deploy-42", new string('a', 32),
            "https://test.example/", "safe", issued);
        var trigger = new SignedAutomationTrigger("deploy-42", new string('a', 32),
            "https://other.example/", "safe", issued, signature);

        Assert.Throws<ArgumentException>(() => new AutomationTriggerVerifier(
            new() { SigningSecret = Secret }, new FixedTimeProvider(now)).Verify(trigger));
        Assert.Throws<InvalidOperationException>(() => new AutomationTriggerVerifier(
            new(), new FixedTimeProvider(now)).Verify(trigger));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
