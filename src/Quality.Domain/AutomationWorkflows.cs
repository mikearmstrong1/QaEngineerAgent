namespace Quality.Domain;

public enum AutomationWorkflowStatus
{
    Triggered,
    Planned,
    Inspected,
    ManifestPrepared,
    PolicyEvaluated,
    AwaitingReview,
    Approved,
    Queued,
    Executed,
    EvidencePublished,
    Classified,
    Completed,
    Failed,
    Cancelled
}

public sealed record AutomationCheckpoint(AutomationWorkflowStatus Status, DateTimeOffset RecordedAt,
    string StageKey, string? Detail = null);

public sealed record AutomationWorkflow(
    string Id,
    string IdempotencyKeyHash,
    string InputHash,
    string JobId,
    string Target,
    AutomationWorkflowStatus Status,
    string PolicyName,
    string PolicyVersion,
    string PolicyHash,
    string PolicySnapshot,
    string Environment,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AutomationCheckpoint[] Checkpoints,
    string? LeaseToken = null,
    DateTimeOffset? LeaseUntil = null,
    int StageAttempts = 0,
    DateTimeOffset? NextAttemptAt = null,
    string? InspectionJson = null,
    string? ManifestJson = null,
    string? ManifestHash = null,
    string? ExecutionRequestId = null,
    string? RunId = null,
    string? Classification = null,
    string? ReviewDecision = null,
    string? Reviewer = null,
    DateTimeOffset? ReviewedAt = null,
    string? EscalationReason = null,
    string? Error = null,
    string SchemaVersion = "1.0")
{
    public bool IsTerminal => Status is AutomationWorkflowStatus.Completed
        or AutomationWorkflowStatus.Failed or AutomationWorkflowStatus.Cancelled;
}
