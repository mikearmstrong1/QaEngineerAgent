using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quality.Domain;

namespace Quality.Orchestrator;

public static class RegressionCatalogService
{
    public static RegressionSuite CreateSuite(string name)
    {
        name = Required(name, nameof(name), 200);
        return new RegressionSuite("SUITE-" + HashParts([name])[..24], name, []);
    }

    public static RegressionVersion CreateVersion(RegressionSuite suite, Requirement requirement,
        TestPlan plan, TestRun run, string reviewedManifestHash, DateTimeOffset createdAt)
    {
        ValidatePromotion(requirement, plan, run, reviewedManifestHash);
        var previous = suite.Versions.OrderBy(v => v.Number).LastOrDefault();
        if (previous is not null && previous.Id != suite.ActiveVersionId)
            throw new ArgumentException("The active regression version must be the latest version");

        var cases = plan.TestCases.Select(test =>
        {
            var criteria = test.AcceptanceCriterionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var criterion in criteria)
                if (!requirement.AcceptanceCriteria.Any(item => item.Id == criterion))
                    throw new ArgumentException("A regression case maps to a missing acceptance criterion");
            var id = "CASE-" + HashParts([requirement.Id, test.Id, .. criteria])[..24];
            return new RegressionCase(id, requirement.Id, requirement.Reference, requirement.Title,
                test.Id, test.Title, criteria);
        }).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

        var number = (previous?.Number ?? 0) + 1;
        var versionId = "VERSION-" + HashParts([suite.Id, number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            previous?.Id ?? "", requirement.SourceRevision, plan.Id, run.Id, reviewedManifestHash,
            .. cases.Select(item => item.Id)])[..24];
        return new RegressionVersion(versionId, suite.Id, number, previous?.Id, cases,
            requirement.SourceRevision, plan.Id, run.Id, reviewedManifestHash, createdAt);
    }

    public static RegressionSuite Append(RegressionSuite suite, RegressionVersion version)
    {
        if (version.SuiteId != suite.Id) throw new ArgumentException("Regression version belongs to another suite");
        if (suite.Versions.Any(item => item.Id == version.Id)) return suite;
        var latest = suite.Versions.OrderBy(item => item.Number).LastOrDefault();
        if (version.Number != (latest?.Number ?? 0) + 1 || version.PreviousVersionId != latest?.Id)
            throw new ArgumentException("Regression versions must form an append-only chain");
        return suite with { Versions = [.. suite.Versions, version], ActiveVersionId = version.Id };
    }

    public static StoryLineage[] Lineage(RegressionVersion version) => version.Cases
        .Select(item => new StoryLineage(item.RequirementId, item.Reference, version.SourceRevision,
            [.. item.AcceptanceCriterionIds], item.Id, version.Id, version.TestPlanId, version.TestRunId, version.ManifestHash))
        .OrderBy(item => item.RegressionCaseId, StringComparer.Ordinal).ToArray();

    public static RegressionCatalogExport Export(RegressionVersion version)
    {
        var stories = Lineage(version);
        var content = JsonSerializer.Serialize(new { schemaVersion = "1.0", suiteId = version.SuiteId,
            versionId = version.Id, stories }, ContractJson.Options) + "\n";
        return new RegressionCatalogExport(version.SuiteId, version.Id,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), stories, content);
    }

    public static RegressionCatalogMetrics Metrics(RegressionVersion version, IEnumerable<TestRun> runs)
    {
        var caseIds = version.Cases.Select(item => item.TestCaseId).ToHashSet(StringComparer.Ordinal);
        var latest = runs.Where(run => run.TestPlanId == version.TestPlanId && run.FinishedAt is not null)
            .OrderByDescending(run => run.FinishedAt).FirstOrDefault();
        var results = latest?.TestResults?.Where(result => caseIds.Contains(result.TestCaseId))
            .GroupBy(result => result.TestCaseId, StringComparer.Ordinal).Select(group => group.Last()).ToArray() ?? [];
        var executed = results.Length;
        var passed = results.Count(result => result.Status == "Passed");
        var failed = results.Count(result => result.Status is "Failed" or "TimedOut" or "InfrastructureFailed");
        return new RegressionCatalogMetrics(version.SuiteId, version.Id,
            version.Cases.Select(item => item.RequirementId).Distinct(StringComparer.Ordinal).Count(), caseIds.Count,
            executed, passed, failed, Ratio(executed, caseIds.Count), Ratio(passed, executed));
    }

    public static JobLineage BuildJobLineage(QualityJob job, IEnumerable<ExecutionRequest> executionRequests,
        IEnumerable<TestRun> runs, IEnumerable<RegressionSuite> suites)
    {
        if (job.Requirement is null || job.TestPlan is null)
            throw new ArgumentException("Completed requirement and test plan are required for lineage");
        var requests = executionRequests.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var requestByRun = requests.Where(item => item.RunId is not null).GroupBy(item => item.RunId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var lineageRuns = runs.OrderBy(item => item.StartedAt).ThenBy(item => item.Id, StringComparer.Ordinal).Select(run =>
        {
            requestByRun.TryGetValue(run.Id, out var request);
            var evidence = (run.StoredArtifacts ?? []).Select(item => new LineageEvidence(item.LocalKey, item.ObjectKey,
                item.Verification?.ChecksumVerified == true, item.Verification?.RedactionStatus ?? "Unverified",
                item.Verification?.VerifiedAt)).OrderBy(item => item.LocalKey, StringComparer.Ordinal).ToArray();
            var verified = evidence.Length > 0 && evidence.All(item => item.ChecksumVerified &&
                (string.Equals(item.RedactionStatus, "Verified", StringComparison.OrdinalIgnoreCase) ||
                 item.RedactionStatus.EndsWith("ScanPassed", StringComparison.Ordinal)));
            return new LineageRun(run.Id, run.Status, run.StartedAt, run.FinishedAt, request?.Id,
                request?.ParentExecutionRequestId, request?.RootExecutionRequestId, request?.RerunAttempt ?? 0,
                run.ManifestHash, evidence, verified);
        }).ToArray();
        var matching = suites.SelectMany(suite => suite.Versions
            .Where(version => version.TestPlanId == job.TestPlan.Id || version.Cases.Any(item => item.RequirementId == job.Requirement.Id))
            .Select(version => (suite, version))).ToArray();
        var versions = matching.Select(item => new LineageCatalogVersion(item.suite.Id, item.suite.Name, item.version.Id,
            item.version.Number, item.version.SourceRevision, item.suite.ActiveVersionId == item.version.Id,
            !string.Equals(item.version.SourceRevision, job.Requirement.SourceRevision, StringComparison.Ordinal),
            Export(item.version).Sha256, item.version.CreatedAt))
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.VersionId, StringComparer.Ordinal).ToArray();
        var currentActive = matching.Where(item => item.suite.ActiveVersionId == item.version.Id &&
            string.Equals(item.version.SourceRevision, job.Requirement.SourceRevision, StringComparison.Ordinal)).ToArray();
        var coveredCriteria = currentActive.SelectMany(item => item.version.Cases).SelectMany(item => item.AcceptanceCriterionIds)
            .ToHashSet(StringComparer.Ordinal);
        var gaps = job.Requirement.AcceptanceCriteria.Where(item => !coveredCriteria.Contains(item.Id))
            .Select(item => item.Id).Order(StringComparer.Ordinal).ToArray();
        return new JobLineage(job.Id, job.Requirement.Reference, job.Requirement.SourceRevision,
            job.Requirement.AcceptanceCriteria, job.TestPlan.TestCases, requests, lineageRuns, versions,
            versions.Length > 0, versions.Any(item => item.Active && item.SourceRevisionStale), gaps);
    }

    private static void ValidatePromotion(Requirement requirement, TestPlan plan, TestRun run, string hash)
    {
        if (requirement.IsStub || plan.IsStub) throw new ArgumentException("Synthetic coverage cannot enter the regression catalog");
        if (plan.RequirementId != requirement.Id || run.TestPlanId != plan.Id)
            throw new ArgumentException("Requirement, plan, and run lineage must match");
        if (run.Status != "Passed" || run.FinishedAt is null || string.IsNullOrWhiteSpace(hash) || run.ManifestHash != hash)
            throw new ArgumentException("A completed passing run and its reviewed manifest hash are required");
        if (run.TestCaseIds is null || !run.TestCaseIds.Order(StringComparer.Ordinal)
                .SequenceEqual(plan.TestCases.Select(item => item.Id).Order(StringComparer.Ordinal)))
            throw new ArgumentException("The executed and planned regression cases must match");
    }

    private static string Required(string value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new ArgumentException($"{name} must contain 1-{max} non-control characters");
        return value.Trim();
    }

    private static string HashParts(IEnumerable<string> parts)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
}
