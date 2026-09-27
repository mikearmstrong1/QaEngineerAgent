using Quality.Orchestrator;

namespace Quality.Api;

public sealed class AutomationScheduleWorker(AutomationScheduleService schedules,
    ILogger<AutomationScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await schedules.ProcessNextDueAsync(stoppingToken) is null)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Automation schedule occurrence failed ({Type}); its deterministic trigger remains retryable",
                    ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
