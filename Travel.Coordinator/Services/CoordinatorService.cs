using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Travel.Coordinator.Data;
using Travel.Coordinator.Models;

namespace Travel.Coordinator.Services;

// IMPORTANT: This tutorial supports exactly ONE Coordinator process.
// SemaphoreSlim protects one transaction inside that process, not across replicas.
public sealed class CoordinatorService
{
    private static readonly string[] Participants = ["Airline", "Hotel"];
    private readonly IDbContextFactory<CoordinatorDbContext> _dbFactory;
    private readonly ParticipantHttpClient _participants;
    private readonly RecoverySettings _settings;
    private readonly DemoFaultSwitches _faults;
    private readonly ILogger<CoordinatorService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public CoordinatorService(
        IDbContextFactory<CoordinatorDbContext> dbFactory,
        ParticipantHttpClient participants,
        RecoverySettings settings,
        DemoFaultSwitches faults,
        ILogger<CoordinatorService> logger)
    {
        _dbFactory = dbFactory;
        _participants = participants;
        _settings = settings;
        _faults = faults;
        _logger = logger;
    }

    private SemaphoreSlim Gate(Guid id) =>
        _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task<List<TravelTransaction>> GetRecentAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Transactions.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc).Take(30).ToListAsync(ct);
    }

    public async Task<TravelTransaction> BeginAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tx = new TravelTransaction
        {
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(_settings.ActiveTtlSeconds)
        };
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var local = await db.Database.BeginTransactionAsync(ct);
        db.Transactions.Add(tx);
        db.TransactionLogs.Add(new TransactionLog
        {
            TransactionId = tx.Id,
            EventType = "STARTED",
            Details = $"ExpiresAtUtc={tx.ExpiresAtUtc:O}"
        });
        await db.SaveChangesAsync(ct);
        await local.CommitAsync(ct);
        return tx;
    }

    public async Task<TravelTransaction?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<List<TransactionLog>> GetLogsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TransactionLogs.AsNoTracking()
            .Where(x => x.TransactionId == id)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
    }

    // Called by RetryWorker. No HTTP request is sent when this list is empty.
    public async Task<List<Guid>> GetDueIdsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Transactions.AsNoTracking()
            .Where(x => (x.Status == TransactionStatus.COMMIT_DECIDED ||
                         x.Status == TransactionStatus.ABORT_DECIDED) &&
                        (x.NextRetryAtUtc == null || x.NextRetryAtUtc <= now))
            .OrderBy(x => x.NextRetryAtUtc)
            .Select(x => x.Id)
            .Take(_settings.BatchSize)
            .ToListAsync(ct);
    }

    // Called by CleanupWorker. Only ACTIVE transactions that have expired.
    public async Task<List<Guid>> GetExpiredActiveIdsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Transactions.AsNoTracking()
            .Where(x => x.Status == TransactionStatus.ACTIVE && x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc)
            .Select(x => x.Id)
            .Take(_settings.BatchSize)
            .ToListAsync(ct);
    }

    // Call ONCE before app.Run() and before accepting requests.
    // Under the single-Coordinator assumption, previous PREPARING is abandoned.
    // An ACTIVE transaction is left alone unless its TTL has expired.
    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var candidates = await db.Transactions.AsNoTracking()
            .Where(x => x.Status == TransactionStatus.PREPARING ||
                (x.Status == TransactionStatus.ACTIVE && x.ExpiresAtUtc <= now))
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            var gate = Gate(id);
            await gate.WaitAsync(ct);
            try
            {
                var tx = await GetAsync(id, ct);
                if (tx is null) continue;
                if (tx.Status == TransactionStatus.PREPARING)
                {
                    await PersistStateAsync(id, TransactionStatus.PREPARING,
                        TransactionStatus.ABORT_DECIDED, "DECISION_ABORT_STARTUP", ct: ct);
                }
                else if (tx.Status == TransactionStatus.ACTIVE &&
                         tx.ExpiresAtUtc <= DateTime.UtcNow)
                {
                    await PersistStateAsync(id, TransactionStatus.ACTIVE,
                        TransactionStatus.ABORT_DECIDED, "DECISION_ABORT_EXPIRED_STARTUP", ct: ct);
                }
            }
            finally { gate.Release(); }
        }
        // RetryWorker will send durable ABORT/COMMIT decisions after startup.
    }

    public async Task<TravelTransaction?> EndAsync(Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var tx = await GetAsync(id, ct);
            if (tx is null) return null;

            // If a failed in-process attempt left PREPARING, only this request
            // can see it while holding the gate. No durable COMMIT exists.
            if (tx.Status == TransactionStatus.PREPARING)
            {
                await PersistStateAsync(id, TransactionStatus.PREPARING,
                    TransactionStatus.ABORT_DECIDED, "DECISION_ABORT_RESUME", ct: ct);
                if (!_faults.PauseNotifications) await NotifyDecisionAsync(id, ct);
                return await GetAsync(id, ct);
            }

            // A repeated END cannot change an existing decision.
            if (tx.Status != TransactionStatus.ACTIVE) return tx;

            // Do not allow an expired ACTIVE transaction to enter Phase 1.
            if (tx.ExpiresAtUtc <= DateTime.UtcNow)
            {
                await PersistStateAsync(id, TransactionStatus.ACTIVE,
                    TransactionStatus.ABORT_DECIDED, "DECISION_ABORT_EXPIRED_END", ct: ct);
                if (!_faults.PauseNotifications) await NotifyDecisionAsync(id, ct);
                return await GetAsync(id, ct);
            }

            await PersistStateAsync(id, TransactionStatus.ACTIVE,
                TransactionStatus.PREPARING, "PREPARING", ct: ct);

            var votes = await Task.WhenAll(
                SafePrepareAsync("Airline", id, ct),
                SafePrepareAsync("Hotel", id, ct));

            bool allYes = votes.All(v => v == "YES");
            var next = allYes
                ? TransactionStatus.COMMIT_DECIDED
                : TransactionStatus.ABORT_DECIDED;

            // Essential invariant: persist the global decision BEFORE any HTTP
            // COMMIT/ABORT message is sent to participants.
            await PersistStateAsync(id, TransactionStatus.PREPARING,
                next, allYes ? "DECISION_COMMIT" : "DECISION_ABORT",
                details: $"Airline={votes[0]};Hotel={votes[1]}", ct: ct);

            // Development switch: demonstrate crash after the durable decision.
            if (!_faults.PauseNotifications) await NotifyDecisionAsync(id, ct);
            return await GetAsync(id, ct);
        }
        finally { gate.Release(); }
    }

    // null: not found; false: COMMIT already decided; true: accepted/already aborted.
    public async Task<bool?> AbortAsync(Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var tx = await GetAsync(id, ct);
            if (tx is null) return null;
            if (tx.Status is TransactionStatus.COMMIT_DECIDED or TransactionStatus.COMMITTED)
                return false;

            // If END is running, this ABORT must wait for the same gate.
            // Therefore an observed PREPARING here is an abandoned attempt.
            if (tx.Status is TransactionStatus.ACTIVE or TransactionStatus.PREPARING)
            {
                await PersistStateAsync(id, tx.Status,
                    TransactionStatus.ABORT_DECIDED, "DECISION_ABORT_CLIENT", ct: ct);
                if (!_faults.PauseNotifications) await NotifyDecisionAsync(id, ct);
            }
            return true;
        }
        finally { gate.Release(); }
    }

    // CleanupWorker calls this only for expired ACTIVE transactions.
    public async Task ExpireActiveAsync(Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var tx = await GetAsync(id, ct);
            if (tx?.Status != TransactionStatus.ACTIVE ||
                tx.ExpiresAtUtc > DateTime.UtcNow) return;

            await PersistStateAsync(id, TransactionStatus.ACTIVE,
                TransactionStatus.ABORT_DECIDED, "DECISION_ABORT_EXPIRED", ct: ct);
            if (!_faults.PauseNotifications) await NotifyDecisionAsync(id, ct);
        }
        finally { gate.Release(); }
    }

    // Called by RetryWorker for only DUE decided transactions.
    public async Task RecoverAsync(Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (_faults.PauseNotifications) return;
            var tx = await GetAsync(id, ct);
            if (tx is null || tx.Status is not
                (TransactionStatus.COMMIT_DECIDED or TransactionStatus.ABORT_DECIDED)) return;
            if (tx.NextRetryAtUtc > DateTime.UtcNow) return;
            await NotifyDecisionAsync(id, ct);
        }
        finally { gate.Release(); }
    }

    // Development-only helpers for deterministic recovery demonstrations.
    public async Task<bool> ForceExpireForDemoAsync(Guid id)
    {
        var gate = Gate(id);
        await gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var local = await db.Database.BeginTransactionAsync();
            var tx = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id);
            if (tx?.Status != TransactionStatus.ACTIVE) return false;
            tx.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            tx.UpdatedAtUtc = DateTime.UtcNow;
            db.TransactionLogs.Add(new TransactionLog
            {
                TransactionId = id,
                EventType = "DEV_FORCE_EXPIRED"
            });
            await db.SaveChangesAsync();
            await local.CommitAsync();
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ForcePreparingForDemoAsync(Guid id)
    {
        var gate = Gate(id);
        await gate.WaitAsync();
        try
        {
            var tx = await GetAsync(id);
            if (tx?.Status != TransactionStatus.ACTIVE) return false;
            await PersistStateAsync(id, TransactionStatus.ACTIVE,
                TransactionStatus.PREPARING, "DEV_FORCE_PREPARING");
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<string> SafePrepareAsync(string participant, Guid id, CancellationToken ct)
    {
        try
        {
            return await _participants.PrepareAsync(participant, id, ct) ? "YES" : "NO";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown is not a NO vote.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Participant} no PREPARE response for {Id}", participant, id);
            return "NO_RESPONSE";
        }
    }

    private async Task NotifyDecisionAsync(Guid id, CancellationToken ct)
    {
        var tx = await GetAsync(id, ct);
        if (tx is null) return;
        bool commit = tx.Status == TransactionStatus.COMMIT_DECIDED;
        if (!commit && tx.Status != TransactionStatus.ABORT_DECIDED) return;

        foreach (var participant in Participants)
        {
            tx = await GetAsync(id, ct) ?? throw new InvalidOperationException();
            bool acked = participant == "Airline" ? tx.AirlineAck : tx.HotelAck;
            if (acked) continue;

            try
            {
                if (commit) await _participants.CommitAsync(participant, id, ct);
                else await _participants.AbortAsync(participant, id, ct);
                // The participant may process the decision but lose its ACK.
                // Retrying is safe ONLY if Student 2's RM is idempotent.
                await PersistAckAsync(id, participant, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Id}: no valid ACK from {Participant}", id, participant);
            }
        }

        tx = await GetAsync(id, ct) ?? throw new InvalidOperationException();
        if (tx.AirlineAck && tx.HotelAck)
        {
            await PersistStateAsync(id,
                commit ? TransactionStatus.COMMIT_DECIDED : TransactionStatus.ABORT_DECIDED,
                commit ? TransactionStatus.COMMITTED : TransactionStatus.ABORTED,
                "FINISHED", ct: ct);
        }
        else await ScheduleRetryAsync(id, ct);
    }

    private async Task PersistStateAsync(
        Guid id, TransactionStatus expected, TransactionStatus next,
        string eventType, string? details = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var local = await db.Database.BeginTransactionAsync(ct);
        var tx = await db.Transactions.SingleAsync(x => x.Id == id, ct);
        if (tx.Status != expected)
            throw new InvalidOperationException($"Invalid state transition {tx.Status} -> {next}");

        tx.Status = next;
        tx.UpdatedAtUtc = DateTime.UtcNow;
        tx.NextRetryAtUtc = next is TransactionStatus.COMMIT_DECIDED or TransactionStatus.ABORT_DECIDED
            ? DateTime.UtcNow : null;
        db.TransactionLogs.Add(new TransactionLog
        {
            TransactionId = id,
            EventType = eventType,
            Details = details
        });
        await db.SaveChangesAsync(ct);
        await local.CommitAsync(ct);
        _logger.LogInformation("{Id}: {EventType}", id, eventType);
    }

    private async Task PersistAckAsync(Guid id, string participant, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var local = await db.Database.BeginTransactionAsync(ct);
        var tx = await db.Transactions.SingleAsync(x => x.Id == id, ct);
        if (tx.Status is not (TransactionStatus.COMMIT_DECIDED or TransactionStatus.ABORT_DECIDED))
            throw new InvalidOperationException("No global decision");

        if (participant == "Airline") tx.AirlineAck = true;
        else if (participant == "Hotel") tx.HotelAck = true;
        else throw new ArgumentOutOfRangeException(nameof(participant));
        tx.UpdatedAtUtc = DateTime.UtcNow;
        db.TransactionLogs.Add(new TransactionLog
        {
            TransactionId = id,
            EventType = $"ACK_{participant.ToUpperInvariant()}"
        });
        await db.SaveChangesAsync(ct);
        await local.CommitAsync(ct);
    }

    private async Task ScheduleRetryAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var local = await db.Database.BeginTransactionAsync(ct);
        var tx = await db.Transactions.SingleAsync(x => x.Id == id, ct);
        if (tx.Status is not (TransactionStatus.COMMIT_DECIDED or TransactionStatus.ABORT_DECIDED))
            return;

        // 2, 4, 8, 16, 30 seconds; max 30. Small demo omits random jitter.
        var seconds = Math.Min(30, (int)Math.Pow(2, Math.Min(tx.RetryCount + 1, 10)));
        tx.RetryCount += 1;
        tx.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(seconds);
        tx.UpdatedAtUtc = DateTime.UtcNow;
        db.TransactionLogs.Add(new TransactionLog
        {
            TransactionId = id,
            EventType = "RETRY_SCHEDULED",
            Details = $"Attempt={tx.RetryCount};DelaySeconds={seconds}"
        });
        await db.SaveChangesAsync(ct);
        await local.CommitAsync(ct);
    }
}
