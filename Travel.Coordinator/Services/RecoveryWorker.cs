namespace Travel.Coordinator.Services;

// Retries ONLY durable decisions: COMMIT_DECIDED / ABORT_DECIDED.
// Startup recovery of PREPARING happens separately, before app.Run().
public sealed class RecoveryWorker(
    CoordinatorService coordinator,
    RecoverySettings settings,
    DemoFaultSwitches faults,
    ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverOnceAsync(stoppingToken);
            using var timer = new PeriodicTimer(
                TimeSpan.FromSeconds(settings.RetryScanSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RecoverOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. Decisions remain durable in SQLite.
        }
    }

    private async Task RecoverOnceAsync(CancellationToken ct)
    {
        if (faults.PauseNotifications) return; // Development test switch.
        try
        {
            var ids = await coordinator.GetDueIdsAsync(ct);
            foreach (var id in ids)
            {
                try { await coordinator.RecoverAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retry failed for {TransactionId}", id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retry scan failed");
        }
    }
}
