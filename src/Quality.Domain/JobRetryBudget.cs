namespace Quality.Domain;

// Persisted policy and usage. Limits are snapshotted at submission, never reset by a new worker.
public sealed record JobRetryBudget(int MaxWorkerAttempts = 5, int MaxNormalizationAttempts = 3,
    int MaxDurationSeconds = 900, int InitialDelayMilliseconds = 250, int MaxDelayMilliseconds = 5000,
    int WorkerAttempts = 0, DateTimeOffset? DeadlineAt = null, DateTimeOffset? LastClaimedAt = null)
{
    public void Validate()
    {
        if (MaxWorkerAttempts is < 1 or > 100 || MaxNormalizationAttempts is < 1 or > 10 ||
            MaxDurationSeconds is < 1 or > 86400 || InitialDelayMilliseconds is < 1 or > 60000 ||
            MaxDelayMilliseconds < InitialDelayMilliseconds || MaxDelayMilliseconds > 60000 || WorkerAttempts < 0)
            throw new ArgumentException("Retry budget limits are outside supported bounds");
    }
}
