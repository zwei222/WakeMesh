using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;

namespace WakeMesh.Api.Services;

/// <summary>
/// Enqueues push work inside the caller's DB transaction and drains after commit.
/// Settings save success is independent of FCM outcome.
/// Drain uses atomic DB lease claim (owner + expiry); network I/O never runs inside a DB transaction.
/// Hosted periodic drain is best-effort with bounded backoff when unconfigured; request path and
/// server restart also recover pending outbox rows (no hard always-on sleep SLA).
/// </summary>
public sealed class PushDispatchService
{
    public const string SettingsChangedType = PushMessageTypes.SettingsChanged;

    private readonly WakeMeshDbContext _db;
    private readonly IPushSender _sender;
    private readonly NotificationStatusService _status;
    private readonly FcmOptions _options;
    private readonly ILogger<PushDispatchService> _logger;

    public PushDispatchService(
        WakeMeshDbContext db,
        IPushSender sender,
        NotificationStatusService status,
        IOptions<FcmOptions> options,
        ILogger<PushDispatchService> logger)
    {
        _db = db;
        _sender = sender;
        _status = status;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Call inside an open write transaction after device DesiredRevision bumps.
    /// Skips revoked devices and devices without tokens. Does not send.
    /// </summary>
    public async Task EnqueueSettingsChangedAsync(IEnumerable<Guid> deviceIds, CancellationToken ct)
    {
        var ids = deviceIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // Even when FCM is not configured we may skip enqueue to avoid useless rows.
        // Spec: unconfigured must not fake send success — enqueue is optional when disabled.
        if (!_options.Configured)
        {
            return;
        }

        var devices = await _db.Devices
            .Where(d => ids.Contains(d.Id))
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var device in devices)
        {
            if (device.Status == DeviceStatus.Revoked)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(device.PushToken))
            {
                continue;
            }

            _db.PushOutbox.Add(new PushOutboxEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                DesiredRevision = device.DesiredRevision,
                MessageType = SettingsChangedType,
                Status = PushOutboxStatuses.Pending,
                AttemptCount = 0,
                CreatedAt = now,
                UpdatedAt = now,
                NextAttemptAt = now,
            });
        }
    }

    /// <summary>
    /// After successful settings commit: bounded drain then a status for the API response.
    /// Failures never throw to the caller — settings already committed.
    /// </summary>
    public async Task<NotificationStatusDto> NotifyAfterCommitAsync(CancellationToken ct)
    {
        if (!_options.Configured || !_sender.IsAvailable)
        {
            return NotificationStatusService.NotConfigured();
        }

        try
        {
            var summary = await DrainPendingAsync(ct);
            return summary.ToNotificationStatus();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Push drain after commit failed ({ErrorType}); outbox retained for retry.",
                ex.GetType().Name);
            return NotificationStatusService.Pending("drainError");
        }
    }

    /// <summary>
    /// Bounded drain of due pending/failed/expired-lease outbox rows.
    /// Safe on host start, periodic worker, and request path.
    /// Only the atomic claim winner sends; attempt count increments at claim time.
    /// </summary>
    public async Task<PushDrainSummary> DrainPendingAsync(CancellationToken ct)
    {
        if (!_options.Configured || !_sender.IsAvailable)
        {
            return PushDrainSummary.NotConfigured;
        }

        var batchSize = Math.Clamp(_options.DrainBatchSize <= 0 ? 32 : _options.DrainBatchSize, 1, 100);
        var maxAttempts = Math.Clamp(_options.MaxAttempts <= 0 ? 8 : _options.MaxAttempts, 1, 32);
        var leaseSeconds = Math.Clamp(_options.LeaseSeconds <= 0 ? 60 : _options.LeaseSeconds, 5, 600);
        var now = DateTimeOffset.UtcNow;
        var leaseOwner = Guid.NewGuid().ToString("N");

        // Fetch broadly then filter in memory — SQLite DateTimeOffset predicates are limited;
        // keep complex OR/AND lease logic off the provider translator.
        var candidates = await _db.PushOutbox.AsNoTracking()
            .Where(o =>
                o.Status == PushOutboxStatuses.Pending
                || o.Status == PushOutboxStatuses.Failed
                || o.Status == PushOutboxStatuses.Sending)
            .ToListAsync(ct);

        var due = candidates
            .Where(o => IsClaimable(o, now))
            .OrderBy(o => o.CreatedAt)
            .Take(batchSize)
            .ToList();

        if (due.Count == 0)
        {
            return new PushDrainSummary(0, 0, 0, 0, 0);
        }

        var accepted = 0;
        var failed = 0;
        var invalid = 0;
        var discarded = 0;
        var attempted = 0;

        foreach (var snap in due)
        {
            ct.ThrowIfCancellationRequested();
            var id = snap.Id;

            var claimNow = DateTimeOffset.UtcNow;
            var leaseUntil = claimNow.AddSeconds(leaseSeconds);

            // Re-read before claim so backoff / lease generation is evaluated on fresh state
            // (stale candidate lists must not send after a competing drain advanced the row).
            var observed = await _db.PushOutbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
            if (observed is null || !IsClaimable(observed, claimNow))
            {
                continue;
            }

            // Atomic claim + attempt increment. No network inside this update.
            // WHERE matches the full observed generation so a concurrent completer/claimer wins.
            var claimed = await TryClaimObservedGenerationAsync(
                observed,
                leaseOwner,
                claimNow,
                leaseUntil,
                ct);

            if (claimed != 1)
            {
                continue;
            }

            attempted++;

            var item = await _db.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == id, ct);
            var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == item.DeviceId, ct);

            if (device is null || device.Status == DeviceStatus.Revoked || string.IsNullOrWhiteSpace(device.PushToken))
            {
                var err = device?.Status == DeviceStatus.Revoked ? "deviceRevoked" : "noToken";
                if (await TryCompleteAsync(
                        id,
                        leaseOwner,
                        PushOutboxStatuses.Discarded,
                        errorCode: err,
                        acceptedAt: null,
                        nextAttemptAt: null,
                        clearLease: true,
                        ct))
                {
                    discarded++;
                }

                continue;
            }

            // Snapshot sent token — InvalidToken clear is conditional on this exact value still stored.
            var sentToken = device.PushToken;
            var notificationCapable = device.PushNotificationsCapable;
            PushSendResult result;
            try
            {
                result = await _sender.SendSettingsChangedAsync(
                        sentToken,
                        device.Id,
                        item.DesiredRevision,
                        notificationCapable,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fixed type only — never log exception object/message (may contain secrets).
                _logger.LogWarning(
                    "Push sender threw for outbox {OutboxId}: {ErrorType}",
                    id,
                    ex.GetType().Name);
                result = new PushSendResult(PushSendOutcome.TransientFailure, ex.GetType().Name);
            }

            var doneAt = DateTimeOffset.UtcNow;

            switch (result.Outcome)
            {
                case PushSendOutcome.Accepted:
                    if (await TryCompleteAsync(
                            id,
                            leaseOwner,
                            PushOutboxStatuses.Accepted,
                            errorCode: null,
                            acceptedAt: doneAt,
                            nextAttemptAt: null,
                            clearLease: true,
                            ct))
                    {
                        accepted++;
                    }

                    break;

                case PushSendOutcome.InvalidToken:
                    // Owner-CAS completion and token-matched clear share one DB transaction.
                    // Clear only when this drain still owns the lease — expired/late owners are inert.
                    // No read-then-delete TOCTOU: both writes are conditional ExecuteUpdate predicates.
                    if (await TryCompleteInvalidTokenAndClearAsync(
                            id,
                            item.DeviceId,
                            leaseOwner,
                            sentToken,
                            result.ErrorCode ?? "invalidToken",
                            doneAt,
                            ct))
                    {
                        invalid++;
                        discarded++;
                    }

                    break;

                case PushSendOutcome.NotConfigured:
                    if (await TryCompleteAsync(
                            id,
                            leaseOwner,
                            PushOutboxStatuses.Pending,
                            errorCode: "notConfigured",
                            acceptedAt: null,
                            nextAttemptAt: doneAt.AddMinutes(5),
                            clearLease: true,
                            ct))
                    {
                        failed++;
                    }

                    break;

                default:
                {
                    var errorCode = result.ErrorCode ?? "transientFailure";
                    if (item.AttemptCount >= maxAttempts)
                    {
                        if (await TryCompleteAsync(
                                id,
                                leaseOwner,
                                PushOutboxStatuses.Discarded,
                                errorCode: errorCode,
                                acceptedAt: null,
                                nextAttemptAt: null,
                                clearLease: true,
                                ct))
                        {
                            discarded++;
                        }
                    }
                    else
                    {
                        // Exponential-ish backoff: 30s * 2^(attempt-1), cap 30m.
                        var seconds = Math.Min(1800, 30 * (1 << Math.Min(item.AttemptCount - 1, 6)));
                        if (await TryCompleteAsync(
                                id,
                                leaseOwner,
                                PushOutboxStatuses.Failed,
                                errorCode: errorCode,
                                acceptedAt: null,
                                nextAttemptAt: doneAt.AddSeconds(seconds),
                                clearLease: true,
                                ct))
                        {
                            failed++;
                        }
                    }

                    break;
                }
            }
        }

        return new PushDrainSummary(attempted, accepted, failed, invalid, discarded);
    }

    private static bool IsClaimable(PushOutboxEntity o, DateTimeOffset now)
    {
        if (o.Status is PushOutboxStatuses.Pending or PushOutboxStatuses.Failed)
        {
            var due = o.NextAttemptAt is null || o.NextAttemptAt <= now;
            // Unowned, or prior owner lease expired (crash / stuck sending was reset to failed).
            var leaseFree = string.IsNullOrEmpty(o.LeaseOwner)
                            || o.LeaseExpiresAt is null
                            || o.LeaseExpiresAt <= now;
            return due && leaseFree;
        }

        if (o.Status == PushOutboxStatuses.Sending)
        {
            return o.LeaseExpiresAt is not null && o.LeaseExpiresAt <= now;
        }

        return false;
    }

    private async Task<int> TryClaimObservedGenerationAsync(
        PushOutboxEntity observed,
        string leaseOwner,
        DateTimeOffset claimNow,
        DateTimeOffset leaseUntil,
        CancellationToken ct)
    {
        // Full generation match: Status/AttemptCount/NextAttemptAt/LeaseExpiresAt/LeaseOwner.
        // Equality (including null) avoids SQLite DateTimeOffset inequality translator limits.
        // Miss → caller continues to the next candidate; no send on stale snapshot.
        return await _db.PushOutbox
            .Where(o => o.Id == observed.Id
                        && o.Status == observed.Status
                        && o.AttemptCount == observed.AttemptCount
                        && o.NextAttemptAt == observed.NextAttemptAt
                        && o.LeaseExpiresAt == observed.LeaseExpiresAt
                        && o.LeaseOwner == observed.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(o => o.Status, PushOutboxStatuses.Sending)
                    .SetProperty(o => o.LeaseOwner, leaseOwner)
                    .SetProperty(o => o.LeaseExpiresAt, leaseUntil)
                    .SetProperty(o => o.AttemptCount, o => o.AttemptCount + 1)
                    .SetProperty(o => o.UpdatedAt, claimNow),
                ct);
    }

    /// <summary>
    /// Completes an outbox row only if this drain still owns the lease (late owner cannot overwrite a newer claim).
    /// </summary>
    private async Task<bool> TryCompleteAsync(
        Guid id,
        string leaseOwner,
        string status,
        string? errorCode,
        DateTimeOffset? acceptedAt,
        DateTimeOffset? nextAttemptAt,
        bool clearLease,
        CancellationToken ct)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        // Always clear lease on successful owner completion (clearLease retained for call-site clarity).
        _ = clearLease;
        var rows = await _db.PushOutbox
            .Where(o => o.Id == id && o.LeaseOwner == leaseOwner && o.Status == PushOutboxStatuses.Sending)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(o => o.Status, status)
                    .SetProperty(o => o.LastErrorCode, errorCode)
                    .SetProperty(o => o.AcceptedAt, acceptedAt)
                    .SetProperty(o => o.NextAttemptAt, nextAttemptAt)
                    .SetProperty(o => o.UpdatedAt, updatedAt)
                    .SetProperty(o => o.LeaseOwner, (string?)null)
                    .SetProperty(o => o.LeaseExpiresAt, (DateTimeOffset?)null),
                ct);

        return rows == 1;
    }

    /// <summary>
    /// Owner-CAS discard + rotation-safe token clear in one transaction.
    /// Token clear runs only after outbox CAS succeeds; failed CAS rolls back with zero token side effects.
    /// </summary>
    private async Task<bool> TryCompleteInvalidTokenAndClearAsync(
        Guid outboxId,
        Guid deviceId,
        string leaseOwner,
        string sentToken,
        string errorCode,
        DateTimeOffset doneAt,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var completed = await TryCompleteAsync(
                outboxId,
                leaseOwner,
                PushOutboxStatuses.Discarded,
                errorCode: errorCode,
                acceptedAt: null,
                nextAttemptAt: null,
                clearLease: true,
                ct);
            if (!completed)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            // Conditional on the exact token this drain sent (rotation-safe).
            await _db.Devices
                .Where(d => d.Id == deviceId && d.PushToken == sentToken)
                .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.PushToken, (string?)null)
                        .SetProperty(d => d.PushTokenUpdatedAt, doneAt),
                    ct);

            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(ct);
            }
            catch
            {
                // ignore secondary rollback failures
            }

            throw;
        }
    }
}

public sealed record PushDrainSummary(int Attempted, int Accepted, int Failed, int InvalidToken, int Discarded)
{
    public static PushDrainSummary NotConfigured { get; } = new(0, 0, 0, 0, 0);

    public NotificationStatusDto ToNotificationStatus()
    {
        if (Attempted == 0)
        {
            return NotificationStatusService.NoTargets();
        }

        if (Accepted > 0 && Failed == 0 && InvalidToken == 0)
        {
            return NotificationStatusService.Accepted(
                $"fcmAccepted={Accepted}; deviceApplyRequiresAck=true");
        }

        if (Accepted > 0)
        {
            return NotificationStatusService.Accepted(
                $"fcmAccepted={Accepted}; failed={Failed}; invalidToken={InvalidToken}; deviceApplyRequiresAck=true");
        }

        if (Failed > 0)
        {
            return NotificationStatusService.Failed($"failed={Failed}; discarded={Discarded}");
        }

        if (Discarded > 0)
        {
            return NotificationStatusService.NoTargets($"discarded={Discarded}");
        }

        return NotificationStatusService.Pending();
    }
}
