using System;
using Quality.Orchestrator;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Quality.Tests
{
    public class JobMetricsTests
    {
        [Fact]
        public async Task TrackAsync_ActiveCount_Transitions()
        {
            var metrics = new JobMetrics();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int activeDuring = -1;

            var task = metrics.TrackAsync(MetricOperation.Plan, async () =>
            {
                activeDuring = metrics.Render().Contains("quality_operations_active{operation=\"plan\"} 1") ? 1 : 0;
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return 42;
            });

            // TrackAsync starts the action before yielding; no timing assumption is needed.
            Assert.Equal(1, activeDuring);

            tcs.SetResult(0);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(42, result);
            Assert.Contains("quality_operations_active{operation=\"plan\"} 0", metrics.Render());
        }

        [Fact]
        public async Task TrackAsync_Exception_RethrowsAndRedacts()
        {
            var metrics = new JobMetrics();
            var secret = "SECRET_TOKEN_123";
            var ex = new InvalidOperationException($"Failed: {secret}");

            var act = async () => await metrics.TrackAsync<int>(MetricOperation.Plan, () => throw ex);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(act);

            Assert.Same(ex, thrown);
            var output = metrics.Render();
            Assert.DoesNotContain(secret, output);
            Assert.Contains("quality_operations_total{operation=\"plan\",outcome=\"error\"} 1", output);
        }

        [Fact]
        public async Task TrackAsync_Concurrent_SuccessCount()
        {
            var metrics = new JobMetrics();
            var tasks = new Task<int>[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = metrics.TrackAsync(MetricOperation.Plan, async () =>
                {
                    await Task.Delay(1);
                    return 42;
                });
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            var output = metrics.Render();
            Assert.Contains("quality_operations_total{operation=\"plan\",outcome=\"success\"} 100", output);
        }
    }
}
