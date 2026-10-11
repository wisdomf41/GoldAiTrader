namespace GoldAiTrader.MT5.BridgeHost;

public sealed class MT5BridgeHealthSupervisor(
    MT5BridgeHealthMonitor monitor,
    MT5BridgeHostOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.HealthSupervisionInterval, timeProvider);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                monitor.Observe();
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the pending timer wait.
        }
    }
}
