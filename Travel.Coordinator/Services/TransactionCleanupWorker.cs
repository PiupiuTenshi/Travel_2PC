namespace Travel.Coordinator.Services;

// Periodically handles ONLY expired ACTIVE transactions.
// It never aborts a healthy, in-flight PREPARING transaction.
public sealed class TransactionCleanupWorker(
    CoordinatorService coordinator,
    RecoverySettings settings,
    ILogger<TransactionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CleanupOnceAsync(stoppingToken);
            using var timer = new PeriodicTimer(
                TimeSpan.FromSeconds(settings.CleanupScanSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CleanupOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. Data is already persisted in SQLite.
        }
    }

    private async Task CleanupOnceAsync(CancellationToken ct)
    {
        try
        {
            var ids = await coordinator.GetExpiredActiveIdsAsync(ct);
            foreach (var id in ids)
            {
                try { await coordinator.ExpireActiveAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Cleanup failed for {TransactionId}", id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "ACTIVE cleanup scan failed");
        }
    }
}
