namespace Quality.Domain;

public sealed record RegressionSuite(string Id, string Name, RegressionVersion[] Versions,
    string? ActiveVersionId = null, string SchemaVersion = "1.0");

public sealed record RegressionCase(string Id, string RequirementId, RequirementReference Reference,
    string RequirementTitle, string TestCaseId, string TestTitle, string[] AcceptanceCriterionIds);

public sealed record RegressionVersion(string Id, string SuiteId, int Number, string? PreviousVersionId,
    RegressionCase[] Cases, string SourceRevision, string TestPlanId, string TestRunId,
    string ManifestHash, DateTimeOffset CreatedAt, string SchemaVersion = "1.0");

public sealed record StoryLineage(string RequirementId, RequirementReference Reference, string SourceRevision,
    string[] AcceptanceCriterionIds, string RegressionCaseId, string RegressionVersionId,
    string TestPlanId, string TestRunId, string ManifestHash);

public sealed record RegressionCatalogExport(string SuiteId, string VersionId, string Sha256,
    StoryLineage[] Stories, string Content, string ContentType = "application/json");

public sealed record RegressionCatalogMetrics(string SuiteId, string VersionId, int StoryCount,
    int RegressionCaseCount, int ExecutedCaseCount, int PassedCaseCount, int FailedCaseCount,
    double ExecutionCoverage, double PassRate);

public sealed record LineageEvidence(string LocalKey, string? ObjectKey, bool ChecksumVerified,
    string RedactionStatus, DateTimeOffset? VerifiedAt);
public sealed record LineageRun(string Id, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? ExecutionRequestId, string? ParentExecutionRequestId, string? RootExecutionRequestId, int RerunAttempt,
    string? ManifestHash, LineageEvidence[] Evidence, bool EvidenceVerified);
public sealed record LineageCatalogVersion(string SuiteId, string SuiteName, string VersionId, int Number,
    string SourceRevision, bool Active, bool SourceRevisionStale, string Sha256, DateTimeOffset CreatedAt);
public sealed record JobLineage(string JobId, RequirementReference Reference, string CurrentSourceRevision,
    AcceptanceCriterion[] AcceptanceCriteria, TestCase[] PlannedTests, ExecutionRequest[] ExecutionRequests,
    LineageRun[] Runs, LineageCatalogVersion[] CatalogVersions, bool HasCatalogCoverage,
    bool SourceRevisionDrift, string[] CoverageGaps);
