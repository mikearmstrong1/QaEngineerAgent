using Quality.Orchestrator;

namespace Quality.Api;

public sealed class ExecutionWorker(ExecutionRequestService executions, ILogger<ExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await executions.ProcessNextAsync(stoppingToken) is null)
                    await Task.Delay(250, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Execution worker attempt failed ({Type}); durable request remains inspectable", ex.GetType().Name);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
