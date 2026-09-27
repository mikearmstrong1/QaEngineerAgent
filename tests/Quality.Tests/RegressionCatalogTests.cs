using System.Text.Json;
using Quality.Domain;
using Quality.Orchestrator;
using Xunit;

namespace Quality.Tests;

public sealed class RegressionCatalogTests
{
    private static (Requirement Requirement, TestPlan Plan, TestRun Run) Fixture()
    {
        var reference = new RequirementReference("jira", "STORY-42");
        var requirement = new Requirement("jira:STORY-42", reference, "Checkout", "Pay for an order", [], [],
            [new("AC-2", "Receipt is shown"), new("AC-1", "Payment is accepted")], [], "jira-rev-7", false);
        var tests = new[]
        {
            new TestCase("TC-2", requirement.Id, "Receipt", "HappyPath", "P1", ["AC-2"], [new("Pay", "Receipt")]),
            new TestCase("TC-1", requirement.Id, "Payment", "HappyPath", "P1", ["AC-1"], [new("Submit", "Accepted")])
        };
        var plan = new TestPlan("plan-7", requirement.Id, "Checkout coverage", tests, [], [], "plan/v2", false);
        var run = new TestRun("run-7", plan.Id, "Passed", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:01:00Z"), [], "executor/1", ManifestHash: new string('a', 64),
            TestCaseIds: ["TC-1", "TC-2"], TestResults:
            [new("TC-1", "Passed", "", 10), new("TC-2", "Failed", "ApplicationFailure", 20)]);
        return (requirement, plan, run);
    }

    [Fact]
    public void StableIdsIgnoreInputCaseOrder()
    {
        var fixture = Fixture();
        var suite = RegressionCatalogService.CreateSuite("Critical journeys");
        var first = RegressionCatalogService.CreateVersion(suite, fixture.Requirement, fixture.Plan, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch);
        var reversed = fixture.Plan with { TestCases = fixture.Plan.TestCases.Reverse().ToArray() };
        var second = RegressionCatalogService.CreateVersion(suite, fixture.Requirement, reversed, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Cases.Select(item => item.Id), second.Cases.Select(item => item.Id));
    }

    [Fact]
    public void AppendBuildsImmutableVersionChain()
    {
        var fixture = Fixture();
        var empty = RegressionCatalogService.CreateSuite("Critical journeys");
        var one = RegressionCatalogService.CreateVersion(empty, fixture.Requirement, fixture.Plan, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch);
        var populated = RegressionCatalogService.Append(empty, one);
        var changedRequirement = fixture.Requirement with { SourceRevision = "jira-rev-8" };
        var two = RegressionCatalogService.CreateVersion(populated, changedRequirement, fixture.Plan, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch.AddDays(1));
        var updated = RegressionCatalogService.Append(populated, two);
        Assert.Empty(empty.Versions);
        Assert.Single(populated.Versions);
        Assert.Equal(2, updated.Versions.Length);
        Assert.Equal(one.Id, two.PreviousVersionId);
        Assert.Equal(two.Id, updated.ActiveVersionId);
    }

    [Fact]
    public void AppendRejectsForksAndForeignSuites()
    {
        var fixture = Fixture();
        var suite = RegressionCatalogService.CreateSuite("Critical journeys");
        var version = RegressionCatalogService.CreateVersion(suite, fixture.Requirement, fixture.Plan, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch);
        var other = RegressionCatalogService.CreateSuite("Other");
        Assert.Throws<ArgumentException>(() => RegressionCatalogService.Append(other, version));
        Assert.Throws<ArgumentException>(() => RegressionCatalogService.Append(suite, version with { Number = 2 }));
    }

    [Theory]
    [InlineData("stub")]
    [InlineData("failed")]
    [InlineData("hash")]
    [InlineData("cases")]
    [InlineData("criterion")]
    public void CatalogFailsClosedForUnverifiedCoverage(string kind)
    {
        var fixture = Fixture();
        var requirement = fixture.Requirement;
        var plan = fixture.Plan;
        var run = fixture.Run;
        var hash = run.ManifestHash!;
        if (kind == "stub") requirement = requirement with { IsStub = true };
        if (kind == "failed") run = run with { Status = "Failed" };
        if (kind == "hash") hash = new string('b', 64);
        if (kind == "cases") run = run with { TestCaseIds = ["TC-1"] };
        if (kind == "criterion") plan = plan with { TestCases = [plan.TestCases[0] with { AcceptanceCriterionIds = ["missing"] }, plan.TestCases[1]] };
        Assert.Throws<ArgumentException>(() => RegressionCatalogService.CreateVersion(
            RegressionCatalogService.CreateSuite("Critical journeys"), requirement, plan, run, hash, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void LineageAndExportAreDeterministicAndRoundTrip()
    {
        var fixture = Fixture();
        var suite = RegressionCatalogService.CreateSuite("Critical journeys");
        var version = RegressionCatalogService.CreateVersion(suite, fixture.Requirement, fixture.Plan, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch);
        var first = RegressionCatalogService.Export(version);
        var second = RegressionCatalogService.Export(version);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Content, second.Content);
        Assert.All(first.Stories, item => Assert.Equal(fixture.Requirement.SourceRevision, item.SourceRevision));
        using var json = JsonDocument.Parse(first.Content);
        Assert.Equal(version.Id, json.RootElement.GetProperty("versionId").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("stories").GetArrayLength());
    }

    [Fact]
    public void MetricsUseLatestCompletedRunAndMappedCasesOnly()
    {
        var fixture = Fixture();
        var suite = RegressionCatalogService.CreateSuite("Critical journeys");
        var version = RegressionCatalogService.CreateVersion(suite, fixture.Requirement, fixture.Plan, fixture.Run,
            fixture.Run.ManifestHash!, DateTimeOffset.UnixEpoch);
        var partial = fixture.Run with { Id = "run-latest", FinishedAt = fixture.Run.FinishedAt!.Value.AddMinutes(1),
            TestResults = [new("TC-1", "Passed", "", 10), new("not-mapped", "Passed", "", 1)] };
        var metrics = RegressionCatalogService.Metrics(version, [fixture.Run, partial]);
        Assert.Equal(1, metrics.StoryCount);
        Assert.Equal(2, metrics.RegressionCaseCount);
        Assert.Equal(1, metrics.ExecutedCaseCount);
        Assert.Equal(.5, metrics.ExecutionCoverage);
        Assert.Equal(1, metrics.PassRate);
    }
}
