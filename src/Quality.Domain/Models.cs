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
public enum ProviderOperationStatus { Started, Completed, Failed, OutcomeUnknown, Cancelled }
public sealed record ProviderOperation(string Id, string Stage, string Provider, string InputHash,
    ProviderOperationStatus Status, int Attempts, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null,
    string? Error = null, DateTimeOffset? RetryAt = null);
public enum JobStatus { Queued, Normalizing, Planning, Completed, Failed, Cancelled }
public sealed record JobTransition(JobStatus Status, DateTimeOffset At, string Reason);
public sealed record QualityJob(string Id, RequirementReference Reference, JobStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision,
    Requirement? Requirement, TestPlan? TestPlan, AgentDecision[] Decisions,
    JobTransition[] Transitions, string? Error, string? LeaseToken, DateTimeOffset? LeaseUntil,
    string SchemaVersion = "1.0", string? SubmissionKeyHash = null, ProviderOperation[]? ProviderOperations = null, JobRetryBudget? RetryBudget = null)
{
    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
    public static QualityJob Create(JobRequest request, DateTimeOffset now)
    {
        request.Validate();
        return new(Guid.NewGuid().ToString("N"), request.Reference, JobStatus.Queued, now, now, 0,
            null, null, [], [new(JobStatus.Queued, now, "Job accepted")], null, null, null);
    }
    // Called under the store's atomic claim lock/transaction, using the store's clock.
    public QualityJob Claim(DateTimeOffset now, TimeSpan lease)
    {
        var budget = RetryBudget ?? new JobRetryBudget();
        budget.Validate();
        var job = this with { RetryBudget = budget, Revision = Revision + 1 };
        var exhausted = budget.DeadlineAt <= now ? "job_time_budget_exhausted"
            : budget.WorkerAttempts >= budget.MaxWorkerAttempts ? "job_retry_budget_exhausted" : null;
        if (exhausted is not null)
            return job.ExhaustBudget(exhausted, now) with { LeaseToken = null, LeaseUntil = null };
        budget = budget with { WorkerAttempts = budget.WorkerAttempts + 1,
            DeadlineAt = budget.DeadlineAt ?? now.AddSeconds(budget.MaxDurationSeconds), LastClaimedAt = now };
        return job with { RetryBudget = budget, LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now + lease };
    }

    public QualityJob ExhaustBudget(string code, DateTimeOffset now)
    {
        var operations = ProviderOperations?.Select(operation => operation.Status != ProviderOperationStatus.Started ? operation
            : operation with { Status = operation.Stage == "Plan" ? ProviderOperationStatus.OutcomeUnknown : ProviderOperationStatus.Failed,
                FinishedAt = now, RetryAt = null, Error = operation.Stage == "Plan" ? "provider_operation_outcome_unknown" : code }).ToArray();
        return (this with { Error = code, ProviderOperations = operations })
            .TransitionTo(JobStatus.Failed, now, "Retry or processing-time budget exhausted");
    }

    public QualityJob Cancel(DateTimeOffset now)
    {
        if (IsTerminal) return this;
        var operations = ProviderOperations?.Select(operation => operation.Status != ProviderOperationStatus.Started ? operation
            : operation with { Status = operation.Stage == "Plan" ? ProviderOperationStatus.OutcomeUnknown : ProviderOperationStatus.Cancelled,
                FinishedAt = now, RetryAt = null, Error = operation.Stage == "Plan" ? "provider_operation_outcome_unknown" : "job_cancelled" }).ToArray();
        return (this with { Error = "job_cancelled", ProviderOperations = operations, LeaseToken = null, LeaseUntil = null })
            .TransitionTo(JobStatus.Cancelled, now, "Job cancelled by request");
    }

    public QualityJob TransitionTo(JobStatus next, DateTimeOffset now, string reason)
    {
        var valid = (Status, next) switch
        {
            (JobStatus.Queued, JobStatus.Normalizing) => true,
            (JobStatus.Normalizing, JobStatus.Planning) when Requirement is not null => true,
            (JobStatus.Planning, JobStatus.Completed) when TestPlan is not null => true,
            (_, JobStatus.Failed or JobStatus.Cancelled) when !IsTerminal => true,
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Invalid transition {Status} -> {next}");
        return this with { Status = next, UpdatedAt = now, Transitions = [..Transitions, new(next, now, reason)] };
    }
}
