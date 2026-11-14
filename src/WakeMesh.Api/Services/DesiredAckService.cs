using Microsoft.EntityFrameworkCore;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;

namespace WakeMesh.Api.Services;

public sealed class DesiredAckService
{
    public static readonly TimeSpan ReceiptTtl = TimeSpan.FromDays(7);
    private readonly WakeMeshDbContext _db;

    public DesiredAckService(WakeMeshDbContext db)
    {
        _db = db;
    }

    public async Task<(int StatusCode, object Body)> GetDesiredAsync(Guid deviceId, string? credential, CancellationToken ct)
    {
        var auth = await AuthenticateDeviceAsync(deviceId, credential, allowRetiring: true, ct);
        if (auth.StatusCode != 200)
        {
            return (auth.StatusCode, auth.Body!);
        }

        // Build snapshot + receipt atomically at a single observed revision.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var device = await _db.Devices.FirstAsync(d => d.Id == deviceId, ct);

            var revision = device.DesiredRevision;
            var items = await BuildItemsAsync(deviceId, ct);
            var tombs = await _db.Tombstones.AsNoTracking()
                .Where(t => t.DeviceId == deviceId && !t.Cleared)
                .Select(t => new DesiredTombstoneDto(t.ScheduleId))
                .ToListAsync(ct);

            // Double-check revision did not move mid-read.
            // Reload resets tracked entity state — re-apply Pending→Active after it.
            await _db.Entry(device).ReloadAsync(ct);
            if (device.DesiredRevision != revision)
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            if (device.Status == DeviceStatus.Pending)
            {
                device.Status = DeviceStatus.Active;
            }

            // Drop items stamped after the observed revision (should not happen, but keep snapshot coherent).
            items = items.Where(i => i.ModifiedRevision <= revision).ToList();

            var receipt = new SnapshotReceiptEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                Revision = revision,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.Add(ReceiptTtl),
                ConsumedSuccess = false,
            };
            _db.SnapshotReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (200, new DesiredSnapshotDto(deviceId, revision, receipt.Id, items, tombs));
        }

        return (409, new { error = "snapshotBusy" });
    }

    public async Task<(int StatusCode, object Body)> PostAckAsync(Guid deviceId, string? credential, AckRequest request, CancellationToken ct)
    {
        var auth = await AuthenticateDeviceAsync(deviceId, credential, allowRetiring: true, ct);
        if (auth.StatusCode != 200)
        {
            return (auth.StatusCode, auth.Body!);
        }

        var result = (request.Result ?? "").Trim().ToLowerInvariant();
        if (result is not ("success" or "failure"))
        {
            return (400, new { error = "invalidResult" });
        }

        var failures = request.Failures ?? Array.Empty<AckFailureDto>();
        if (result == "success" && failures.Count > 0)
        {
            // Partial success is not allowed: callers must report failure with item details.
            return (400, new { error = "successWithFailures" });
        }

        // Retry on transient races with PUT/activation (never surface 500 for concurrency).
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return await PostAckOnceAsync(deviceId, request, result, failures, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
            }
        }

        // Last attempt — still treat as accepted if prior success exists; else conflict.
        _db.ChangeTracker.Clear();
        var applied = await _db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.AppliedRevision)
            .FirstAsync(ct);
        return (409, new AckResponse(deviceId, applied, "concurrencyConflict"));
    }

    private async Task<(int StatusCode, object Body)> PostAckOnceAsync(
        Guid deviceId,
        AckRequest request,
        string result,
        IReadOnlyList<AckFailureDto> failures,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null)
        {
            return (401, new { error = "unauthorized" });
        }

        if (device.Status is DeviceStatus.Revoked)
        {
            return (410, new { error = "deviceRevoked" });
        }

        if (device.Status == DeviceStatus.Pending)
        {
            // Activation without bumping concurrency against concurrent writers.
            await _db.Devices
                .Where(d => d.Id == deviceId && d.Status == DeviceStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceStatus.Active), ct);
            device.Status = DeviceStatus.Active;
        }

        if (request.Revision > device.DesiredRevision)
        {
            return (422, new { error = "futureRevision" });
        }

        var receipt = await _db.SnapshotReceipts.FirstOrDefaultAsync(r => r.Id == request.ReceiptId, ct);
        if (receipt is null)
        {
            return (409, new { error = "unknownReceipt" });
        }

        if (receipt.DeviceId != deviceId)
        {
            return (409, new { error = "receiptDeviceMismatch" });
        }

        if (receipt.Revision != request.Revision)
        {
            return (409, new { error = "receiptRevisionMismatch" });
        }

        if (receipt.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return (410, new { error = "receiptExpired" });
        }

        var priorSuccess = await _db.AckRecords.AsNoTracking()
            .AnyAsync(a => a.DeviceId == deviceId
                           && a.ReceiptId == request.ReceiptId
                           && a.Revision == request.Revision
                           && a.Result == "success", ct);

        if (priorSuccess)
        {
            // Later failure must not undo success; still record diagnostic failure if any.
            _db.AckRecords.Add(new AckRecordEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                ReceiptId = request.ReceiptId,
                Revision = request.Revision,
                Result = result,
                FailuresJson = JsonUtil.Serialize(failures),
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var applied = await CurrentAppliedAsync(deviceId, ct);
            return (200, new AckResponse(deviceId, applied, "accepted"));
        }

        if (result == "success")
        {
            // Atomic max — concurrent lower revisions cannot regress AppliedRevision.
            await _db.Devices
                .Where(d => d.Id == deviceId && d.AppliedRevision < request.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.AppliedRevision, request.Revision), ct);

            receipt.ConsumedSuccess = true;

            // Clear tombstones only for this revision window; never wipe newer concurrent tombs.
            await _db.Tombstones
                .Where(t => t.DeviceId == deviceId && !t.Cleared && t.CreatedAtRevision <= request.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Cleared, true), ct);

            // Retirement completes only on success ACK of the *current* desired revision while Retiring.
            // Stale pre-retire receipts may still advance AppliedRevision (atomic max above) but must
            // not flip cancelConfirmed / Revoked — both Status=Retiring and DesiredRevision match
            // are required in the atomic WHERE (do not trust the in-memory device snapshot alone).
            await _db.Devices
                .Where(d => d.Id == deviceId
                            && d.Status == DeviceStatus.Retiring
                            && d.DesiredRevision == request.Revision)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DeviceStatus.Revoked)
                    .SetProperty(d => d.CancelConfirmed, true)
                    .SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow), ct);
        }

        _db.AckRecords.Add(new AckRecordEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ReceiptId = request.ReceiptId,
            Revision = request.Revision,
            Result = result,
            FailuresJson = JsonUtil.Serialize(failures),
            ReceivedAt = DateTimeOffset.UtcNow,
        });

        // Avoid saving a stale tracked Device row that could race PUT on RowVersion.
        _db.Entry(device).State = EntityState.Detached;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var finalApplied = await CurrentAppliedAsync(deviceId, ct);
        return (200, new AckResponse(deviceId, finalApplied, "accepted"));
    }

    private async Task<long> CurrentAppliedAsync(Guid deviceId, CancellationToken ct) =>
        await _db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.AppliedRevision)
            .FirstAsync(ct);

    private async Task<(int StatusCode, object? Body, DeviceEntity? Device)> AuthenticateDeviceAsync(
        Guid deviceId,
        string? credential,
        bool allowRetiring,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return (401, new { error = "deviceCredentialRequired" }, null);
        }

        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null)
        {
            // Do not leak existence vs bad credential before auth completes.
            return (401, new { error = "unauthorized" }, null);
        }

        if (!CredentialHasher.Matches(credential, device.CredentialHash))
        {
            return (401, new { error = "unauthorized" }, null);
        }

        if (device.Status == DeviceStatus.Revoked)
        {
            return (410, new { error = "deviceRevoked" }, null);
        }

        if (device.Status == DeviceStatus.Retiring && !allowRetiring)
        {
            return (410, new { error = "deviceRevoked" }, null);
        }

        return (200, null, device);
    }

    private async Task<IReadOnlyList<DesiredItemDto>> BuildItemsAsync(Guid deviceId, CancellationToken ct)
    {
        var schedules = await _db.DeviceSchedules.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .Join(_db.AlarmGroups.AsNoTracking().Where(g => g.DeletedAt == null),
                s => s.GroupId,
                g => g.Id,
                (s, g) => new { s, g })
            .ToListAsync(ct);

        var list = new List<DesiredItemDto>();
        foreach (var row in schedules)
        {
            IReadOnlyList<int>? days = null;
            string? date = null;
            if (string.Equals(row.g.Repeat, "once", StringComparison.OrdinalIgnoreCase))
            {
                var payload = JsonUtil.Deserialize<Dictionary<string, string?>>(row.g.DaysOrDateJson);
                payload.TryGetValue("date", out date);
            }
            else
            {
                days = JsonUtil.Deserialize<List<int>>(row.g.DaysOrDateJson);
            }

            var excluded = JsonUtil.Deserialize<List<string>>(row.g.ExcludedDatesJson);
            list.Add(new DesiredItemDto(
                row.s.Id,
                row.g.Id,
                row.g.Label,
                row.g.Enabled,
                row.g.Repeat,
                days,
                date,
                row.g.TimeZone,
                row.g.ExcludeHolidays,
                excluded,
                row.s.Time,
                row.s.SoundKey,
                row.s.Volume,
                row.s.Vibrate,
                row.s.Enabled,
                row.s.ModifiedRevision));
        }

        return list;
    }
}
