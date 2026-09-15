using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Quality.Domain;

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record RequirementReference(string Source, string Id)
{
    public RequirementReference Validate()
    {
        if (Source is not ("jira" or "coda" or "stub"))
            throw new ArgumentException("source must be jira, coda, or stub");
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 200 || Id.Any(char.IsControl))
            throw new ArgumentException("id must contain 1-200 non-control characters");
        if (Source == "jira" && !Regex.IsMatch(Id, "^[A-Z][A-Z0-9_]*-[1-9][0-9]*$"))
            throw new ArgumentException("Jira id must be an issue key such as AUTH-1427");
        return this;
    }
}
public sealed record JobRequest(RequirementReference Reference)
{
    public JobRequest Validate()
    {
        if (Reference is null) throw new ArgumentException("reference is required");
        Reference.Validate();
        return this;
    }
}
public sealed record SourceProvenance(string Provider, string ResourceId, string Revision, string Field, string Locator);
public sealed record AcceptanceCriterion(string Id, string Description, SourceProvenance? Provenance = null);
public sealed record Requirement(string Id, RequirementReference Reference, string Title,
    string Description, string[] Actors, string[] Preconditions, AcceptanceCriterion[] AcceptanceCriteria,
    string[] Risks, string SourceRevision, bool IsStub, string SchemaVersion = "1.0");
public sealed record TestStep(string Action, string ExpectedResult);
public sealed record TestCase(string Id, string RequirementId, string Title, string Category,
    string Priority, string[] AcceptanceCriterionIds, TestStep[] Steps, string AutomationStatus = "Planned");
public sealed record PlanningMetadata(string Provider, string RequestedModel, string? Model,
    string ProviderVersion, string PromptHash, string SchemaHash, int Attempts, string? ResponseId);
public sealed record TestPlan(string Id, string RequirementId, string Summary, TestCase[] TestCases,
    string[] Assumptions, string[] CoverageGaps, string PromptVersion, bool IsStub, string SchemaVersion = "1.0",
    PlanningMetadata? Planning = null);
public sealed record StoredArtifact(string LocalKey, string ObjectKey, string Bucket, string ContentType, long Length, string Sha256);
public sealed record TestRun(string Id, string TestPlanId, string Status, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string[] ArtifactKeys, string? ExecutorVersion, string SchemaVersion = "1.0",
    string? ManifestHash = null, string[]? TestCaseIds = null,
    StoredArtifact[]? StoredArtifacts = null, string? ArtifactUploadStatus = null,
    string? FailureClassification = null, string? FailureReviewReason = null);
public sealed record FailureAnalysis(string Id, string TestRunId, string Classification,
    string Summary, string[] EvidenceArtifactKeys, double Confidence, bool RequiresReview, string SchemaVersion = "1.0",
    StoredArtifact[]? EvidenceArtifacts = null);
public sealed record AgentDecision(string Id, string JobId, string Stage, string Provider,
    string PromptVersion, string Summary, DateTimeOffset At, bool IsStub, string SchemaVersion = "1.0",
    PlanningMetadata? Planning = null);
public enum JobStatus { Queued, Normalizing, Planning, Completed, Failed }
public sealed record JobTransition(JobStatus Status, DateTimeOffset At, string Reason);
public sealed record QualityJob(string Id, RequirementReference Reference, JobStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision,
    Requirement? Requirement, TestPlan? TestPlan, AgentDecision[] Decisions,
    JobTransition[] Transitions, string? Error, string? LeaseToken, DateTimeOffset? LeaseUntil,
    string SchemaVersion = "1.0")
{
    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed;
    public static QualityJob Create(JobRequest request, DateTimeOffset now)
    {
        request.Validate();
        return new(Guid.NewGuid().ToString("N"), request.Reference, JobStatus.Queued, now, now, 0,
            null, null, [], [new(JobStatus.Queued, now, "Job accepted")], null, null, null);
    }
    public QualityJob TransitionTo(JobStatus next, DateTimeOffset now, string reason)
    {
        var valid = (Status, next) switch
        {
            (JobStatus.Queued, JobStatus.Normalizing) => true,
            (JobStatus.Normalizing, JobStatus.Planning) when Requirement is not null => true,
            (JobStatus.Planning, JobStatus.Completed) when TestPlan is not null => true,
            (_, JobStatus.Failed) when !IsTerminal => true,
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Invalid transition {Status} -> {next}");
        return this with { Status = next, UpdatedAt = now, Transitions = [..Transitions, new(next, now, reason)] };
    }
}
