namespace Travel.Coordinator.Services;

// Demo configuration (single Coordinator process).
// All time spans are in seconds.
public sealed class RecoverySettings
{
    public int ActiveTtlSeconds { get; set; } = 120;
    public int RetryScanSeconds { get; set; } = 5;
    public int CleanupScanSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 25;
}
