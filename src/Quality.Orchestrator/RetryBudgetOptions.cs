using Quality.Domain;
using System.Net;
namespace Quality.Orchestrator;

public sealed record RetryBudgetOptions(int MaxWorkerAttempts = 5, int MaxNormalizationAttempts = 3,
    int MaxDurationSeconds = 900, int InitialDelayMilliseconds = 250, int MaxDelayMilliseconds = 5000)
{
    public JobRetryBudget Snapshot()
    {
        var budget = new JobRetryBudget(MaxWorkerAttempts, MaxNormalizationAttempts, MaxDurationSeconds,
            InitialDelayMilliseconds, MaxDelayMilliseconds);
        budget.Validate();
        return budget;
    }
}

public sealed class RequirementRequestException(HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException("Requirement source request failed", null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
