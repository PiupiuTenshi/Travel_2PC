namespace Travel.Coordinator.Models;

public enum TransactionStatus
{
    ACTIVE,
    PREPARING,
    COMMIT_DECIDED,
    ABORT_DECIDED,
    COMMITTED,
    ABORTED
}

public sealed class TravelTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TransactionStatus Status { get; set; } = TransactionStatus.ACTIVE;
    public bool AirlineAck { get; set; }
    public bool HotelAck { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    // ACTIVE can wait for Client, but not forever.
    public DateTime ExpiresAtUtc { get; set; }
}

// Logical 2PC event log. This is not the same as SQLite's physical WAL file.
public sealed class TransactionLog
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
