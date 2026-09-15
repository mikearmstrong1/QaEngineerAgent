using Quality.Orchestrator;
namespace Quality.Api;
public sealed class JobWorker(JobService jobs, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await jobs.ProcessNextAsync(null, stoppingToken) is null)
                    await Task.Delay(250, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Worker attempt failed ({Type}); durable lease permits recovery", ex.GetType().Name);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
