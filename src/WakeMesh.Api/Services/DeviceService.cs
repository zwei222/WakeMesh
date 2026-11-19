using Microsoft.EntityFrameworkCore;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;

namespace WakeMesh.Api.Services;

public sealed class DeviceService
{
    private readonly WakeMeshDbContext _db;
    private readonly PushDispatchService _push;

    public DeviceService(WakeMeshDbContext db, PushDispatchService push)
    {
        _db = db;
        _push = push;
    }

    public async Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken ct)
    {
        var hash = CredentialHasher.GenerateCredential(out var credential);
        var entity = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "device" : request.DisplayName.Trim(),
            CredentialHash = hash,
            Status = DeviceStatus.Pending,
            DesiredRevision = 0,
            AppliedRevision = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            CancelConfirmed = false,
            RowVersion = 1,
        };
        _db.Devices.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new DeviceRegisterResponse(entity.Id, entity.DisplayName, credential, entity.Status.ToString());
    }

    public async Task<IReadOnlyList<DeviceListItem>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.Devices.AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
        {
            return Array.Empty<DeviceListItem>();
        }

        var succeeded = await LoadDevicesWithSuccessfulSyncAsync(rows.Select(d => d.Id).ToList(), ct);

        return rows
            .OrderBy(d => d.CreatedAt)
            .Select(d => MapListItem(d, succeeded.Contains(d.Id)))
            .ToList();
    }

    /// <summary>
    /// Projects "this registration has durable sync evidence" from persisted ACKs only.
    /// A success counts when the ACK row is <c>result = success</c> AND its receipt exists for the
    /// same device and revision the ACK claims — that receipt join is what keeps ACKs from a
    /// previous registration generation (or a foreign device) from being credited to the current row.
    /// Single set-based query (no per-device N+1) using EXISTS/IN semantics and no DateTimeOffset
    /// comparison/aggregate, so SQLite, PostgreSQL and SQL Server all translate it unchanged.
    /// Receipt TTL is deliberately not evaluated: acknowledgement evidence stays durable after the
    /// 7-day receipt window closes, and treating expiry as "no sync" would recreate the very
    /// false "waiting for initial sync" state this projection exists to remove.
    /// </summary>
    private async Task<HashSet<Guid>> LoadDevicesWithSuccessfulSyncAsync(
        IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        var ids = (await (
                from ack in _db.AckRecords.AsNoTracking()
                where ack.Result == "success" && deviceIds.Contains(ack.DeviceId)
                join receipt in _db.SnapshotReceipts.AsNoTracking() on ack.ReceiptId equals receipt.Id
                where receipt.DeviceId == ack.DeviceId && receipt.Revision == ack.Revision
                select ack.DeviceId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        return ids;
    }

    public async Task<(int StatusCode, object Body)> GetAsync(Guid id, CancellationToken ct)
    {
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return (404, new { error = "notFound" });
        }

        var failures = await LoadRecentFailuresAsync(id, ct);
        return (200, new DeviceDetailDto(
            device.Id,
            device.DisplayName,
            device.Status.ToString(),
            device.DesiredRevision,
            device.AppliedRevision,
            RetirementState(device),
            device.CancelConfirmed,
            device.RevokedAt,
            failures));
    }

    public async Task<(int StatusCode, object? Body)> RetireAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null)
            {
                await tx.RollbackAsync(ct);
                return (404, new { error = "notFound" });
            }

            if (device.Status is DeviceStatus.Revoked or DeviceStatus.Retiring)
            {
                await tx.RollbackAsync(ct);
                return (204, null);
            }

            await PrepareEmptyCancelSnapshotAsync(device, ct);
            device.Status = DeviceStatus.Retiring;
            device.CancelConfirmed = false;
            device.RowVersion += 1;
            await _push.EnqueueSettingsChangedAsync(new[] { device.Id }, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _ = await _push.NotifyAfterCommitAsync(ct);
            return (204, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Concurrent schedule bump / parallel retire — never 500; roll back staged tombstones.
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            var fresh = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (fresh is null)
            {
                return (404, new { error = "notFound" });
            }

            // Parallel retire/revoke already moved the device — idempotent success.
            if (fresh.Status is DeviceStatus.Retiring or DeviceStatus.Revoked)
            {
                return (204, null);
            }

            // Still live but RowVersion raced: typed conflict so the client can reload/retry.
            return (409, new { error = "concurrencyConflict" });
        }
    }

    /// <summary>
    /// Immediate revoke: credential is dead now. Local cancel is unconfirmed until a prior retiring ACK path.
    /// </summary>
    public async Task<(int StatusCode, object? Body)> RevokeImmediateAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return (404, new { error = "notFound" });
        }

        if (device.Status == DeviceStatus.Revoked && device.CancelConfirmed)
        {
            return (204, null);
        }

        if (device.Status != DeviceStatus.Revoked)
        {
            await PrepareEmptyCancelSnapshotAsync(device, ct);
        }

        device.Status = DeviceStatus.Revoked;
        device.RevokedAt ??= DateTimeOffset.UtcNow;
        // Immediate path does not wait for device cancel ACK.
        device.CancelConfirmed = false;
        device.RowVersion += 1;
        // Clear push token so revoked devices are never targeted.
        device.PushToken = null;
        device.PushTokenUpdatedAt = DateTimeOffset.UtcNow;
        device.PushNotificationsCapable = false;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (204, null);
    }

    private async Task PrepareEmptyCancelSnapshotAsync(DeviceEntity device, CancellationToken ct)
    {
        var schedules = await _db.DeviceSchedules.Where(s => s.DeviceId == device.Id).ToListAsync(ct);
        var nextRev = device.DesiredRevision + 1;
        foreach (var s in schedules)
        {
            _db.Tombstones.Add(new DesiredTombstoneEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                ScheduleId = s.Id,
                CreatedAtRevision = nextRev,
                Cleared = false,
            });
        }

        _db.DeviceSchedules.RemoveRange(schedules);
        device.DesiredRevision = nextRev;
    }

    private async Task<IReadOnlyList<DeviceAckFailureDto>> LoadRecentFailuresAsync(Guid deviceId, CancellationToken ct)
    {
        // Order in-memory: SQLite cannot ORDER BY DateTimeOffset expressions.
        var rows = (await _db.AckRecords.AsNoTracking()
                .Where(a => a.DeviceId == deviceId && a.Result == "failure")
                .ToListAsync(ct))
            .OrderByDescending(a => a.ReceivedAt)
            .Take(20)
            .ToList();

        var list = new List<DeviceAckFailureDto>();
        foreach (var row in rows)
        {
            List<AckFailureDto> failures;
            try
            {
                failures = JsonUtil.Deserialize<List<AckFailureDto>>(row.FailuresJson);
            }
            catch
            {
                failures = new List<AckFailureDto>();
            }

            if (failures.Count == 0)
            {
                list.Add(new DeviceAckFailureDto(row.ReceiptId, row.Revision, null, "failure", null, row.ReceivedAt));
                continue;
            }

            foreach (var f in failures)
            {
                list.Add(new DeviceAckFailureDto(
                    row.ReceiptId,
                    row.Revision,
                    f.ScheduleId,
                    f.Code,
                    f.Message,
                    row.ReceivedAt));
            }
        }

        return list;
    }

    internal static string RetirementState(DeviceEntity d) => d.Status switch
    {
        DeviceStatus.Retiring => "retiring",
        DeviceStatus.Revoked when d.CancelConfirmed => "revoked",
        DeviceStatus.Revoked => "revokedUnconfirmed",
        _ => "none",
    };

    private static DeviceListItem MapListItem(DeviceEntity d, bool hasSuccessfulSync) =>
        new(d.Id, d.DisplayName, d.Status.ToString(), d.DesiredRevision, d.AppliedRevision,
            RetirementState(d), d.CancelConfirmed, d.RevokedAt, hasSuccessfulSync);
}
