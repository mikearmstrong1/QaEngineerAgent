using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;

namespace Quality.Tests;

public sealed class RegressionCatalogStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-regression-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuiteAndVersionsSurviveStoreRestart()
    {
        var store = new FileRegressionCatalogStore(root);
        await store.InitializeAsync(default);
        var suite = RegressionCatalogService.CreateSuite("Checkout journeys");
        await store.CreateAsync(suite, default);
        var (requirement, plan, run) = Fixture();
        var version = RegressionCatalogService.CreateVersion(suite, requirement, plan, run, run.ManifestHash!, DateTimeOffset.UnixEpoch);
        await store.AppendAsync(version, default);

        var reopened = new FileRegressionCatalogStore(root);
        await reopened.InitializeAsync(default);
        var loaded = await reopened.GetAsync(suite.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal(version.Id, loaded.ActiveVersionId);
        var loadedVersion = Assert.Single(loaded.Versions);
        Assert.Equal(version.Id, loadedVersion.Id);
        Assert.Equal(version.Cases.Select(item => item.Id), loadedVersion.Cases.Select(item => item.Id));
        Assert.Equal(suite.Id, Assert.Single(await reopened.ListAsync(default)).Id);
    }

    [Fact]
    public async Task CreatesAndAppendsAreIdempotentButConflictsFailClosed()
    {
        var store = new FileRegressionCatalogStore(root);
        var suite = RegressionCatalogService.CreateSuite("Checkout journeys");
        Assert.Equal(suite.Id, (await store.CreateAsync(suite, default)).Id);
        Assert.Equal(suite.Id, (await store.CreateAsync(suite, default)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(suite with { Id = "SUITE-" + new string('a', 24) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("../catalog", default));

        var (requirement, plan, run) = Fixture();
        var version = RegressionCatalogService.CreateVersion(suite, requirement, plan, run, run.ManifestHash!, DateTimeOffset.UnixEpoch);
        var once = await store.AppendAsync(version, default);
        var twice = await store.AppendAsync(version, default);
        Assert.Equal(once.ActiveVersionId, twice.ActiveVersionId);
        Assert.Equal(once.Versions.Select(item => item.Id), twice.Versions.Select(item => item.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(version with { Id = "VERSION-" + new string('b', 24), Number = 2 }, default));
    }

    private static (Requirement, TestPlan, TestRun) Fixture()
    {
        var reference = new RequirementReference("jira", "SHOP-42");
        var requirement = new Requirement("jira:SHOP-42", reference, "Checkout", "Checkout", [], [],
            [new("AC-1", "Payment succeeds")], [], "rev-1", false);
        var plan = new TestPlan("plan-1", requirement.Id, "Coverage",
            [new("TC-1", requirement.Id, "Payment", "HappyPath", "P1", ["AC-1"], [new("Pay", "Paid")])],
            [], [], "plan/v2", false);
        var run = new TestRun("run-1", plan.Id, "Passed", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1),
            [], "executor/1", ManifestHash: new string('c', 64), TestCaseIds: ["TC-1"]);
        return (requirement, plan, run);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
