using System.Text.Json;
using System.Diagnostics;
using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;

namespace Quality.Tests;

public sealed class ExecutionPolicyTests
{
    [Fact]
    public async Task FileStoreMigratesLegacyPoliciesAndPreservesImmutableRevisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "policies.json");
            var v1 = Policy("v1", autoLaunch: false);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { v1 }, ContractJson.Options));
            var store = new FileExecutionPolicyStore(path);

            await store.InitializeAsync(default);

            var migrated = Assert.Single(await store.ListAsync(default));
            Assert.Equal(ExecutionPolicyStatus.Active, migrated.Status);
            Assert.Equal(v1.Fingerprint(), migrated.Fingerprint);
            await Assert.ThrowsAsync<ExecutionPolicyConflictException>(() =>
                store.CreateAsync(v1 with { MaxTimeoutSeconds = 61 }, false, DateTimeOffset.UtcNow, default));

            var v2 = await store.CreateAsync(Policy("v2", autoLaunch: true), false, DateTimeOffset.UtcNow, default);
            Assert.Equal(ExecutionPolicyStatus.Draft, v2.Status);
            v2 = await store.SetStatusAsync(v2.Name, v2.Version, ExecutionPolicyStatus.Active, DateTimeOffset.UtcNow, default);
            Assert.Equal(ExecutionPolicyStatus.Active, v2.Status);
            Assert.Equal(ExecutionPolicyStatus.Retired, (await store.GetAsync(v1.Name, v1.Version, default))!.Status);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SetStatusAsync(v1.Name, v1.Version, ExecutionPolicyStatus.Active, DateTimeOffset.UtcNow, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisabledSeedIsNotReactivatedOnRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-policy-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileExecutionPolicyStore(Path.Combine(root, "policies.json"));
            var policy = Policy("v1", autoLaunch: false);
            var catalog = new ExecutionPolicyCatalog();
            var service = new ExecutionPolicyService(catalog, store, [policy], TimeProvider.System);
            await service.InitializeAsync(default);
            await service.SetStatusAsync(policy.Name, policy.Version, ExecutionPolicyStatus.Disabled, default);

            service = new ExecutionPolicyService(new ExecutionPolicyCatalog(), store, [policy], TimeProvider.System);
            await service.InitializeAsync(default);

            Assert.Equal(ExecutionPolicyStatus.Disabled, service.Get(policy.Name, policy.Version)!.Status);
            Assert.Empty(service.DescribeActive());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Production", 1, 3600, 1)]
    [InlineData("test", 0, 3600, 1)]
    [InlineData("test", 1, 59, 1)]
    [InlineData("test", 1, 3600, 0)]
    public void PolicyRejectsInvalidEnvironmentAndAutomaticLaunchBudgets(string environment,
        int maximumConcurrent, int windowSeconds, int maximumInWindow)
    {
        var policy = Policy("v1", autoLaunch: true) with
        {
            Environment = environment,
            MaxConcurrentAutoLaunches = maximumConcurrent,
            AutoLaunchWindowSeconds = windowSeconds,
            MaxAutoLaunchesPerWindow = maximumInWindow
        };

        Assert.Throws<ArgumentException>(() => ExecutionPolicyCatalog.ValidatePolicy(policy));
    }

    [Fact]
    public async Task CliCreatesActivatesShowsDisablesAndListsTheSameRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-policy-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var example = Path.Combine(repository, "schemas/examples/execution-policy.json");
            var created = await RunCliAsync(repository, root, "policy-create", "--file", example);
            Assert.Equal(ExecutionPolicyStatus.Draft,
                JsonSerializer.Deserialize<ExecutionPolicyRevision>(created, ContractJson.Options)!.Status);
            var activated = await RunCliAsync(repository, root, "policy-activate", "--name", "local-readonly", "--version", "v1");
            Assert.Equal(ExecutionPolicyStatus.Active,
                JsonSerializer.Deserialize<ExecutionPolicyRevision>(activated, ContractJson.Options)!.Status);
            var shown = await RunCliAsync(repository, root, "policy-show", "--name", "local-readonly", "--version", "v1");
            Assert.Equal(ExecutionPolicyStatus.Active,
                JsonSerializer.Deserialize<ExecutionPolicyRevision>(shown, ContractJson.Options)!.Status);
            var disabled = await RunCliAsync(repository, root, "policy-disable", "--name", "local-readonly", "--version", "v1");
            Assert.Equal(ExecutionPolicyStatus.Disabled,
                JsonSerializer.Deserialize<ExecutionPolicyRevision>(disabled, ContractJson.Options)!.Status);
            using var listed = JsonDocument.Parse(await RunCliAsync(repository, root, "policy-list"));
            Assert.Contains(listed.RootElement.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("name").GetString() == "local-readonly"
                && item.GetProperty("status").GetString() == "Disabled");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FileStoreAtomicallyEnforcesConcurrencyWindowAndLifetimeBudgets()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-policy-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileExecutionRequestStore(root);
            var now = DateTimeOffset.UtcNow;
            var hash = new string('a', 64);
            var concurrent = Enumerable.Range(0, 8).Select(_ => ApprovedRequest(hash, now)).ToArray();
            foreach (var request in concurrent) await store.CreateAsync(request, default);
            var race = await Task.WhenAll(concurrent.Select(request => store.TryReserveAutoLaunchAsync(request.Id,
                request.Revision, new(hash, 100, 1, 3600, 100), now, default)));
            Assert.Single(race, result => result is not null);

            var first = race.Single(result => result is not null)!;
            await store.SaveAsync(first with { Status = ExecutionRequestStatus.Passed }, first.Revision, default);
            var second = concurrent.First(request => request.Id != first.Id);
            var bounded = new AutoLaunchBudget(hash, 2, 1, 60, 1);
            Assert.Null(await store.TryReserveAutoLaunchAsync(second.Id, second.Revision, bounded, now.AddSeconds(30), default));
            var reservedSecond = await store.TryReserveAutoLaunchAsync(second.Id, second.Revision, bounded, now.AddSeconds(61), default);
            Assert.NotNull(reservedSecond);
            await store.SaveAsync(reservedSecond! with { Status = ExecutionRequestStatus.Passed }, reservedSecond.Revision, default);
            var third = concurrent.First(request => request.Id != first.Id && request.Id != second.Id);
            Assert.Null(await store.TryReserveAutoLaunchAsync(third.Id, third.Revision, bounded, now.AddSeconds(122), default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task PostgreSqlActivationIsAtomicAndLeavesOneActiveRevision()
    {
        await MigrationTests.InSchema(async source =>
        {
            var jobs = new PostgresJobStore(source);
            await jobs.InitializeAsync(default);
            var store = new PostgresExecutionPolicyStore(source);
            var policies = Enumerable.Range(1, 8).Select(index => Policy("v" + index, autoLaunch: false)).ToArray();
            foreach (var policy in policies) await store.CreateAsync(policy, false, DateTimeOffset.UtcNow, default);

            await Task.WhenAll(policies.Select(policy => store.SetStatusAsync(policy.Name, policy.Version,
                ExecutionPolicyStatus.Active, DateTimeOffset.UtcNow, default)));

            var saved = await store.ListAsync(default);
            Assert.Single(saved, item => item.Status == ExecutionPolicyStatus.Active);
            Assert.Equal(7, saved.Count(item => item.Status == ExecutionPolicyStatus.Retired));
        });
    }

    [PostgresFact]
    public async Task PostgreSqlAutoLaunchReservationIsAtomic()
    {
        await MigrationTests.InSchema(async source =>
        {
            var jobs = new PostgresJobStore(source);
            await jobs.InitializeAsync(default);
            var job = QualityJob.Create(new(new("stub", "budget")), DateTimeOffset.UtcNow);
            await jobs.CreateAsync(job, default);
            var store = new PostgresExecutionRequestStore(source);
            var now = DateTimeOffset.UtcNow;
            var hash = new string('b', 64);
            var requests = Enumerable.Range(0, 8).Select(_ => ApprovedRequest(hash, now, job.Id)).ToArray();
            foreach (var request in requests) await store.CreateAsync(request, default);

            var reservations = await Task.WhenAll(requests.Select(request => store.TryReserveAutoLaunchAsync(request.Id,
                request.Revision, new(hash, 100, 1, 3600, 100), now, default)));

            Assert.Single(reservations, result => result is not null);
            Assert.Single(await Task.WhenAll(requests.Select(request => store.GetAsync(request.Id, default))),
                request => request!.AutoLaunchReservedAt is not null);
        });
    }

    private static ExecutionPolicy Policy(string version, bool autoLaunch) => new("sandbox", version,
        ["http://127.0.0.1:8000"], ["goto", "expectText", "expectVisible"], 60,
        NonProduction: true, AutoApprove: true, AutoLaunch: autoLaunch,
        CanaryMaxAutoLaunches: autoLaunch ? 1 : 0);

    private static ExecutionRequest ApprovedRequest(string policyHash, DateTimeOffset now, string? jobId = null)
        => new(Guid.NewGuid().ToString("N"), jobId ?? Guid.NewGuid().ToString("N"), "plan", "http://127.0.0.1:8000/",
            ExecutionRequestStatus.Approved, "{}", new string('0', 64), 0, now, now,
            new(new string('0', 64), "autonomous-policy:test@v1", now), AutomationPolicy: "test",
            AutomationPolicyVersion: "v1", AutomationPolicyHash: policyHash);

    private static async Task<string> RunCliAsync(string repository, string dataRoot, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(repository, "src/Quality.Api/bin/Release/net10.0/Quality.Api.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["Quality__Store"] = "File";
        start.Environment["Quality__DataDirectory"] = Path.Combine(dataRoot, "jobs");
        start.Environment["Quality__Execution__RunDirectory"] = Path.Combine(dataRoot, "executions");
        start.Environment["Quality__Execution__Workspace"] = repository;
        start.Environment["Quality__Execution__AllowedOrigins"] = "http://127.0.0.1:5090";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
}
