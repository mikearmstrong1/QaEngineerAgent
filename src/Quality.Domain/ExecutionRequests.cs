namespace Quality.Domain;

public enum ExecutionRequestStatus
{
    Draft,
    AwaitingApproval,
    Approved,
    Queued,
    Running,
    Passed,
    Failed,
    TimedOut,
    InfrastructureFailed,
    Cancelled
}

public sealed record ExecutionApproval(string ManifestHash, string Reviewer, DateTimeOffset ApprovedAt);

public sealed record ExecutionRequest(
    string Id,
    string JobId,
    string TestPlanId,
    string Target,
    ExecutionRequestStatus Status,
    string ManifestJson,
    string ManifestHash,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ExecutionApproval? Approval = null,
    string? RunId = null,
    string? Error = null,
    string SchemaVersion = "1.0",
    string? LeaseToken = null,
    DateTimeOffset? LeaseUntil = null,
    int Attempts = 0,
    string? AutomationPolicy = null,
    string? AutomationPolicyVersion = null,
    string? AutomationPolicyHash = null);
