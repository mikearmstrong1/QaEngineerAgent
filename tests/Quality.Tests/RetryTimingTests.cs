using Quality.Domain;
using Quality.Orchestrator;
using Quality.Persistence;
using Xunit;
namespace Quality.Tests;

public sealed class RetryTimingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyWakeDoesNotCallOrSpendAttemptUntilPersistedDeadline(bool cancelWhileWaiting)
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-timing-" + Guid.NewGuid().ToString("N"));
        var clock = new EarlyWakeClock();
        var store = new FileJobStore(directory, clock);
        var policy = new RetryBudgetOptions(MaxDurationSeconds: 30);
        using var firstCancellation = new CancellationTokenSource();
        using var replacementCancellation = new CancellationTokenSource();
        Task<QualityJob?>? processing = null;
        try
        {
            var interruptedSource = new ReadSource((_, _) =>
            {
                firstCancellation.Cancel();
                return Task.FromCanceled<Requirement>(firstCancellation.Token);
            });
            var original = new JobService(store, interruptedSource, new StubLlmProvider(), clock, retryOptions: policy);
            var job = await original.SubmitAsync(new(new("stub", "early-wake")), default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.ProcessNextAsync(job.Id, firstCancellation.Token));
            var checkpoint = (await store.GetAsync(job.Id, default))!;
            var operation = Assert.Single(checkpoint.ProviderOperations!);
            var due = clock.GetUtcNow() + TimeSpan.FromTicks(5_005_000); // 500.5 ms
            await store.SaveAsync(checkpoint with
            {
                ProviderOperations = [operation with { RetryAt = due }],
                LeaseUntil = clock.GetUtcNow().AddSeconds(-1)
            }, default);
            DateTimeOffset? calledAt = null;
            var source = new ReadSource((reference, ct) =>
            {
                calledAt = clock.GetUtcNow();
                return new StubRequirementSource().NormalizeAsync(reference, ct);
            });
            var replacement = new JobService(store, source, new StubLlmProvider(), clock, retryOptions: policy);
            processing = replacement.ProcessNextAsync(job.Id, replacementCancellation.Token);
            var firstWait = await clock.NextAsync();
            clock.Advance(TimeSpan.FromMilliseconds(500));
            firstWait.Fire(); // Wake 0.5 ms early, regardless of OS scheduling.
            var secondWait = await clock.NextAsync();
            Assert.True(secondWait.DueTime >= TimeSpan.FromMilliseconds(1));
            Assert.Equal(0, source.Calls);
            var waiting = (await store.GetAsync(job.Id, default))!;
            Assert.Equal(1, Assert.Single(waiting.ProviderOperations!).Attempts);
            Assert.Equal(due, Assert.Single(waiting.ProviderOperations!).RetryAt);
            if (cancelWhileWaiting)
            {
                replacementCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, source.Calls);
                Assert.Equal(1, Assert.Single((await store.GetAsync(job.Id, default))!.ProviderOperations!).Attempts);
            }
            else
            {
                clock.Advance(TimeSpan.FromTicks(5_000));
                secondWait.Fire();
                var completed = (await processing.WaitAsync(TimeSpan.FromSeconds(5)))!;
                Assert.Equal(JobStatus.Completed, completed.Status);
                Assert.Equal(1, source.Calls);
                Assert.True(calledAt >= due);
                Assert.Equal(2, completed.ProviderOperations!.Single(o => o.Stage == "Normalize").Attempts);
            }
        }
        finally
        {
            replacementCancellation.Cancel();
            if (processing is not null)
                try { await processing.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ReadSource(Func<RequirementReference, CancellationToken, Task<Requirement>> read) : IRequirementSource
    {
        public int Calls;
        public Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return read(reference, ct); }
    }
}
