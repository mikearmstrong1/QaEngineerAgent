using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class ExecutionTests
{
    private static TestPlan Plan => new("plan-1", "requirement-1", "Proposed plan", [new("TC-1", "requirement-1", "Page content", "HappyPath", "P1", ["AC-1"], [new("Open page", "Heading appears")])], [], [], "plan/v2", false);
    private static ExecutionManifest Manifest => new(Plan.Id, ExecutionManifest.HashPlan(Plan), "http://127.0.0.1:8000/", [new("TC-1", [new("goto", null, "/"), new("expectText", "h1", "Ready")])]);

    [Fact]
    public void ReviewedManifestAcceptsMappedTests()
        => Manifest.Validate(Plan, ["http://127.0.0.1:8000"]);

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("code")]
    [InlineData("assertion")]
    [InlineData("timeout")]
    [InlineData("navigation")]
    public void InvalidManifestFailsBeforeExecution(string kind)
    {
        var manifest = kind switch
        {
            "empty" => Manifest with { Tests = [] },
            "unknown" => Manifest with { Tests = [Manifest.Tests[0] with { TestCaseId = "invented" }] },
            "stale" => Manifest with { PlanHash = new string('0', 64) },
            "duplicate" => Manifest with { Tests = [Manifest.Tests[0], Manifest.Tests[0]] },
            "code" => Manifest with { Tests = [new("TC-1", [new("evaluate", null, "process.exit()"), new("expectVisible", "h1", null)])] },
            "assertion" => Manifest with { Tests = [new("TC-1", [new("goto", null, "/")])] },
            "timeout" => Manifest with { TimeoutSeconds = 301 },
            _ => Manifest with { Tests = [new("TC-1", [new("goto", null, "https://other.invalid"), new("expectVisible", "h1", null)])] }
        };
        Assert.Throws<ArgumentException>(() => manifest.Validate(Plan, ["http://127.0.0.1:8000"]));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8001")]
    [InlineData("http://localhost:8000")]
    [InlineData("https://127.0.0.1:8000")]
    [InlineData("http://127.0.0.1:8000/path")]
    [InlineData("http://user:secret@127.0.0.1:8000")]
    public void AllowlistRequiresExactOrigin(string origin)
        => Assert.Throws<ArgumentException>(() => Manifest.Validate(Plan, [origin]));

    [Fact]
    public async Task UnreviewedAndChangedBytesNeverStartARun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quality-execution-unit-" + Guid.NewGuid().ToString("N"));
        var executor = new PlaywrightTestExecutor(new("missing-workspace", []), new FileTestRunStore(directory), TimeProvider.System);
        await Assert.ThrowsAsync<NotSupportedException>(() => executor.ExecuteAsync(Plan, new("http://localhost"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteReviewedAsync(Plan, [1, 2, 3], "wrong", default));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task RunStatusPersistsAndRejectsPathTraversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quality-run-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileTestRunStore(directory);
            Assert.Throws<ArgumentException>(() => store.DirectoryFor("../outside"));
            var run = new TestRun(Guid.NewGuid().ToString("N"), "plan", "Running", DateTimeOffset.UtcNow, null, [], null);
            await store.SaveAsync(run, default);
            run = run with { Status = "Failed", FinishedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(run, default);
            Assert.Equal("Failed", (await new FileTestRunStore(directory).GetAsync(run.Id, default))!.Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
