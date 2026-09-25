using Quality.Orchestrator;

namespace Quality.Api;

public sealed class AutomationWorkflowWorker(AutomationWorkflowService workflows,
    ILogger<AutomationWorkflowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await workflows.ProcessNextAsync(stoppingToken) is null)
                    await Task.Delay(250, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Automation workflow attempt failed ({Type}); durable checkpoint permits recovery", ex.GetType().Name);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
