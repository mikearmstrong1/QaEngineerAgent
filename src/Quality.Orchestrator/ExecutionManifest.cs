using System.Security.Cryptography;
using System.Text.Json;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed record ExecutionStep(string Action, string? Selector, string? Value);
public sealed record ExecutionTest(string TestCaseId, ExecutionStep[] Steps);
public sealed record ExecutionManifest(string TestPlanId, string PlanHash, string Target,
    ExecutionTest[] Tests, int TimeoutSeconds = 60, string SchemaVersion = "1.0")
{
    public static string HashPlan(TestPlan plan) => Hash(JsonSerializer.SerializeToUtf8Bytes(plan, ContractJson.Options));
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Validate(TestPlan plan, string[] allowedOrigins)
    {
        if (SchemaVersion != "1.0" || TestPlanId != plan.Id || PlanHash != HashPlan(plan))
            throw new ArgumentException("Execution manifest does not match the persisted plan");
        if (TimeoutSeconds is < 1 or > 300) throw new ArgumentException("Execution timeout must be 1-300 seconds");
        if (!Uri.TryCreate(Target, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")
            || target.UserInfo.Length != 0 || target.Fragment.Length != 0)
            throw new ArgumentException("Execution target must be an HTTP(S) URL without credentials or fragment");
        var origins = allowedOrigins.Select(ValidateOrigin).ToArray();
        if (!origins.Contains(target.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
            throw new ArgumentException("Execution target is not allowlisted");
        if (plan.TestCases.Length == 0 || plan.TestCases.Select(t => t.Id).Distinct().Count() != plan.TestCases.Length)
            throw new ArgumentException("Plan must contain tests with unique IDs");
        if (Tests is null || Tests.Length is < 1 or > 50 || Tests.Any(t => t is null)
            || Tests.Select(t => t.TestCaseId).Distinct().Count() != Tests.Length)
            throw new ArgumentException("Manifest must contain 1-50 uniquely mapped tests");
        foreach (var test in Tests)
        {
            var planned = plan.TestCases.SingleOrDefault(t => t.Id == test.TestCaseId);
            if (planned is null || planned.RequirementId != plan.RequirementId || planned.AcceptanceCriterionIds.Length == 0)
                throw new ArgumentException("Every executable test must map to a planned test and acceptance criteria");
            if (test.Steps is null || test.Steps.Length is < 1 or > 100 || test.Steps.Any(s => s is null)
                || !test.Steps.Any(s => s.Action is "expectText" or "expectVisible" or "expectUrl"))
                throw new ArgumentException("Every executable test requires steps and at least one assertion");
            foreach (var step in test.Steps)
            {
                if (step.Action is not ("goto" or "click" or "fill" or "expectText" or "expectVisible" or "expectUrl"))
                    throw new ArgumentException("Unsupported execution action");
                if (step.Selector?.Length > 2000 || step.Value?.Length > 10000)
                    throw new ArgumentException("Execution step is too large");
                if (step.Action is "goto" or "expectUrl")
                {
                    if (step.Selector is not null || !Uri.TryCreate(target, step.Value, out var url)
                        || string.IsNullOrWhiteSpace(step.Value) || url.UserInfo.Length != 0
                        || !origins.Contains(url.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
                        throw new ArgumentException("Navigation and URL assertions must use an allowlisted URL");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(step.Selector)) throw new ArgumentException("Locator is required");
                    if (step.Action is "fill" or "expectText")
                    {
                        if (step.Value is null) throw new ArgumentException("Step value is required");
                    }
                    else if (step.Value is not null) throw new ArgumentException("This step does not accept a value");
                }
            }
        }
    }
    public static string ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Allowed targets must be exact HTTP(S) origins");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public sealed record ExecutionOptions(string Workspace, string[] AllowedOrigins, string NodeExecutable = "node");
public interface IReviewedTestExecutor : ITestExecutor
{
    Task<TestRun> ExecuteReviewedAsync(TestPlan plan, byte[] manifestBytes, string reviewedSha256, CancellationToken ct);
}
public interface ITestRunStore
{
    Task SaveAsync(TestRun run, CancellationToken ct);
    Task<TestRun?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<TestRun>> ListByPlanAsync(string testPlanId, CancellationToken ct);
    string DirectoryFor(string id);
}
