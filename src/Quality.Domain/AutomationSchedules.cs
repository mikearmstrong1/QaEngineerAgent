namespace Quality.Domain;

public sealed record AutomationSchedule(
    string Id,
    string Name,
    string JobId,
    string Target,
    string PolicyName,
    int IntervalSeconds,
    bool Enabled,
    DateTimeOffset NextOccurrenceAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision = 0,
    DateTimeOffset? LastOccurrenceAt = null,
    string? LastWorkflowId = null,
    string? LeaseToken = null,
    DateTimeOffset? LeaseUntil = null,
    DateTimeOffset? ClaimedOccurrenceAt = null);
