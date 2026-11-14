using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;

namespace WakeMesh.Api.Services;

public sealed class AlarmGroupService
{
    private readonly WakeMeshDbContext _db;
    private readonly NotificationStatusService _notifications;
    private readonly PushDispatchService _push;

    public AlarmGroupService(
        WakeMeshDbContext db,
        NotificationStatusService notifications,
        PushDispatchService push)
    {
        _db = db;
        _notifications = notifications;
        _push = push;
    }

    public async Task<IReadOnlyList<AlarmGroupDto>> ListAsync(CancellationToken ct)
    {
        var groups = await _db.AlarmGroups
            .AsNoTracking()
            .Include(g => g.Targets)
            .Where(g => g.DeletedAt == null)
            .OrderBy(g => g.Id)
            .ToListAsync(ct);
        return groups.Select(Map).ToList();
    }

    public async Task<(int StatusCode, object Body)> CreateAsync(AlarmGroupWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return (400, new { error = "idRequired" });
        }

        var scheduleValidation = ValidateScheduleFields(request);
        if (scheduleValidation is not null)
        {
            return scheduleValidation.Value;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var existing = await _db.AlarmGroups.AsNoTracking()
                .Include(g => g.Targets)
                .FirstOrDefaultAsync(g => g.Id == request.Id, ct);
            if (existing is not null)
            {
                return ResolveExistingCreate(existing, request);
            }

            var validation = await ValidateTargetsAsync(request.Targets, ct);
            if (validation is not null)
            {
                return validation.Value;
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var entity = new AlarmGroupEntity
            {
                Id = request.Id,
                Label = request.Label,
                Enabled = request.Enabled,
                Version = 1,
                Repeat = request.Repeat,
                DaysOrDateJson = EncodeDaysOrDate(request),
                TimeZone = request.TimeZone,
                ExcludeHolidays = request.ExcludeHolidays,
                ExcludedDatesJson = JsonUtil.Serialize(request.ExcludedDates ?? Array.Empty<string>()),
                RowVersion = 1,
            };

            var deviceIds = new HashSet<Guid>();
            foreach (var t in request.Targets)
            {
                var schedule = new DeviceScheduleEntity
                {
                    Id = Guid.NewGuid(),
                    GroupId = entity.Id,
                    DeviceId = t.DeviceId,
                    Time = t.Time,
                    SoundKey = t.SoundKey,
                    Volume = t.Volume,
                    Vibrate = t.Vibrate,
                    Enabled = t.Enabled,
                    ModifiedRevision = 0,
                };
                entity.Targets.Add(schedule);
                deviceIds.Add(t.DeviceId);
            }

            _db.AlarmGroups.Add(entity);
            var bump = await BumpDevicesAsync(deviceIds, entity.Targets, ct);
            if (bump is not null)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return bump.Value;
            }

            try
            {
                await _push.EnqueueSettingsChangedAsync(deviceIds, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                var notification = await _push.NotifyAfterCommitAsync(ct);
                return (201, new SaveGroupResult(Map(entity), notification));
            }
            catch (DbUpdateConcurrencyException)
            {
                // Concurrent retire/revoke bumped device RowVersion — do not leave schedules on non-live devices.
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return (400, new { error = "unknownOrRevokedDevice" });
            }
            catch (DbUpdateException) when (IsUniqueViolation())
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                // Concurrent insert of same id — re-read and resolve without leaking content on conflict.
                var raced = await _db.AlarmGroups.AsNoTracking()
                    .Include(g => g.Targets)
                    .FirstOrDefaultAsync(g => g.Id == request.Id, ct);
                if (raced is not null)
                {
                    return ResolveExistingCreate(raced, request);
                }
            }
        }

        return (409, new { error = "conflict" });
    }

    private (int StatusCode, object Body) ResolveExistingCreate(AlarmGroupEntity existing, AlarmGroupWriteRequest request)
    {
        if (existing.DeletedAt is not null)
        {
            // Do not reveal deleted payload.
            return (409, new { error = "conflict" });
        }

        var mapped = Map(existing);
        if (IsSameContent(mapped, request))
        {
            return (200, new SaveGroupResult(mapped, _notifications.GetStatus()));
        }

        // Different content: generic conflict, no body comparison leakage.
        return (409, new { error = "conflict" });
    }

    private static bool IsUniqueViolation()
    {
        // Provider-agnostic: caller already filtered DbUpdateException; treat as unique race.
        return true;
    }

    public async Task<(int StatusCode, object Body)> ReplaceAsync(string id, AlarmGroupWriteRequest request, long? ifMatch, CancellationToken ct)
    {
        if (ifMatch is null)
        {
            return (428, new { error = "ifMatchRequired" });
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var entity = await _db.AlarmGroups.Include(g => g.Targets).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (entity is null || entity.DeletedAt is not null)
        {
            return (404, new { error = "notFound" });
        }

        if (entity.Version != ifMatch.Value)
        {
            return (412, new { error = "versionMismatch", currentVersion = entity.Version });
        }

        var scheduleValidation = ValidateScheduleFields(request);
        if (scheduleValidation is not null)
        {
            return scheduleValidation.Value;
        }

        var validation = await ValidateTargetsAsync(request.Targets, ct);
        if (validation is not null)
        {
            return validation.Value;
        }

        var oldDeviceIds = entity.Targets.Select(t => t.DeviceId).ToHashSet();
        var oldSchedules = entity.Targets.ToDictionary(t => t.DeviceId);

        entity.Label = request.Label;
        entity.Enabled = request.Enabled;
        entity.Repeat = request.Repeat;
        entity.DaysOrDateJson = EncodeDaysOrDate(request);
        entity.TimeZone = request.TimeZone;
        entity.ExcludeHolidays = request.ExcludeHolidays;
        entity.ExcludedDatesJson = JsonUtil.Serialize(request.ExcludedDates ?? Array.Empty<string>());
        entity.Version += 1;
        entity.RowVersion += 1;

        var newDeviceIds = request.Targets.Select(t => t.DeviceId).ToHashSet();
        var removed = oldDeviceIds.Except(newDeviceIds).ToList();

        foreach (var removeDevice in removed)
        {
            var schedule = oldSchedules[removeDevice];
            _db.DeviceSchedules.Remove(schedule);
        }

        var keptOrAdded = new List<DeviceScheduleEntity>();
        foreach (var t in request.Targets)
        {
            if (oldSchedules.TryGetValue(t.DeviceId, out var existingTarget))
            {
                existingTarget.Time = t.Time;
                existingTarget.SoundKey = t.SoundKey;
                existingTarget.Volume = t.Volume;
                existingTarget.Vibrate = t.Vibrate;
                existingTarget.Enabled = t.Enabled;
                keptOrAdded.Add(existingTarget);
            }
            else
            {
                var schedule = new DeviceScheduleEntity
                {
                    Id = Guid.NewGuid(),
                    GroupId = entity.Id,
                    DeviceId = t.DeviceId,
                    Time = t.Time,
                    SoundKey = t.SoundKey,
                    Volume = t.Volume,
                    Vibrate = t.Vibrate,
                    Enabled = t.Enabled,
                };
                entity.Targets.Add(schedule);
                keptOrAdded.Add(schedule);
            }
        }

        var affected = oldDeviceIds.Union(newDeviceIds).ToHashSet();
        var bump = await BumpDevicesAsync(affected, keptOrAdded, ct, tombstoneSchedules: removed.Select(d => oldSchedules[d]).ToList());
        if (bump is not null)
        {
            return bump.Value;
        }

        try
        {
            await _push.EnqueueSettingsChangedAsync(affected, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (412, new { error = "concurrencyConflict" });
        }

        await tx.CommitAsync(ct);
        await _db.Entry(entity).Collection(e => e.Targets).LoadAsync(ct);
        var notification = await _push.NotifyAfterCommitAsync(ct);
        return (200, new SaveGroupResult(Map(entity), notification));
    }

    public async Task<(int StatusCode, object Body)> DeleteAsync(string id, long? ifMatch, CancellationToken ct)
    {
        if (ifMatch is null)
        {
            return (428, new { error = "ifMatchRequired" });
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var entity = await _db.AlarmGroups.Include(g => g.Targets).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (entity is null || entity.DeletedAt is not null)
        {
            return (404, new { error = "notFound" });
        }

        if (entity.Version != ifMatch.Value)
        {
            return (412, new { error = "versionMismatch", currentVersion = entity.Version });
        }

        entity.DeletedAt = DateTimeOffset.UtcNow;
        entity.Enabled = false;
        entity.Version += 1;
        entity.RowVersion += 1;

        var targets = entity.Targets.ToList();
        var deviceIds = targets.Select(t => t.DeviceId).ToHashSet();
        _db.DeviceSchedules.RemoveRange(targets);
        var bump = await BumpDevicesAsync(deviceIds, Array.Empty<DeviceScheduleEntity>(), ct, tombstoneSchedules: targets);
        if (bump is not null)
        {
            return bump.Value;
        }

        try
        {
            await _push.EnqueueSettingsChangedAsync(deviceIds, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (412, new { error = "concurrencyConflict" });
        }

        await tx.CommitAsync(ct);
        // Delete stays 204; push outcome is durable in outbox (not claimed as device apply).
        _ = await _push.NotifyAfterCommitAsync(ct);
        return (204, new { });
    }

    private async Task<(int StatusCode, object Body)?> ValidateTargetsAsync(IReadOnlyList<AlarmTargetDto> targets, CancellationToken ct)
    {
        if (targets.Count == 0)
        {
            return (400, new { error = "targetsRequired" });
        }

        if (targets.Select(t => t.DeviceId).Distinct().Count() != targets.Count)
        {
            return (400, new { error = "duplicateDeviceTarget" });
        }

        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t.Time) || !IsValidHmTime(t.Time))
            {
                return (400, new { error = "invalidTime" });
            }
        }

        var ids = targets.Select(t => t.DeviceId).ToList();
        // Early check only — authoritative status is re-validated inside BumpDevicesAsync under the write transaction.
        var found = await _db.Devices
            .Where(d => ids.Contains(d.Id) && d.Status != DeviceStatus.Revoked && d.Status != DeviceStatus.Retiring)
            .Select(d => d.Id)
            .ToListAsync(ct);
        if (found.Count != ids.Count)
        {
            return (400, new { error = "unknownOrRevokedDevice" });
        }

        return null;
    }

    /// <summary>
    /// Minimal schedule field validation. Weekly requires non-null non-empty days; once keeps date requirement.
    /// Timezone and HH:mm time are required and shape-checked.
    /// </summary>
    private static (int StatusCode, object Body)? ValidateScheduleFields(AlarmGroupWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZone))
        {
            return (400, new { error = "timeZoneRequired" });
        }

        if (!TryValidateTimeZone(request.TimeZone.Trim()))
        {
            return (400, new { error = "invalidTimeZone" });
        }

        if (string.Equals(request.Repeat, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Days is null || request.Days.Count == 0)
            {
                return (400, new { error = "daysRequired" });
            }

            if (request.Days.Any(d => d is < 0 or > 6))
            {
                return (400, new { error = "invalidDays" });
            }
        }
        else if (string.Equals(request.Repeat, "once", StringComparison.OrdinalIgnoreCase))
        {
            // Existing once contract: date required and parseable as yyyy-MM-dd.
            if (string.IsNullOrWhiteSpace(request.Date))
            {
                return (400, new { error = "dateRequired" });
            }

            if (!DateOnly.TryParse(request.Date, out _))
            {
                return (400, new { error = "invalidDate" });
            }
        }
        else
        {
            return (400, new { error = "invalidRepeat" });
        }

        return null;
    }

    private static bool TryValidateTimeZone(string timeZone)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool IsValidHmTime(string time)
    {
        // HH:mm 24h
        if (time.Length != 5 || time[2] != ':')
        {
            return false;
        }

        return int.TryParse(time.AsSpan(0, 2), out var h)
               && int.TryParse(time.AsSpan(3, 2), out var m)
               && h is >= 0 and <= 23
               && m is >= 0 and <= 59;
    }

    /// <summary>
    /// Loads tracked devices inside the caller's transaction, rejects non-live statuses, then bumps revision.
    /// Pending is still assignable (pre-activation); Active is the steady state. Retiring/Revoked are refused
    /// so concurrent retire cannot mix new schedules into a cancel snapshot (paired with Device RowVersion).
    /// SQLite in-memory tests do not prove SQL Server isolation / locking behavior.
    /// </summary>
    private async Task<(int StatusCode, object Body)?> BumpDevicesAsync(
        IEnumerable<Guid> deviceIds,
        IEnumerable<DeviceScheduleEntity> schedulesToStamp,
        CancellationToken ct,
        IReadOnlyList<DeviceScheduleEntity>? tombstoneSchedules = null)
    {
        var idList = deviceIds.Distinct().ToList();
        var devices = await _db.Devices.Where(d => idList.Contains(d.Id)).ToListAsync(ct);

        // Re-read under the write transaction: refuse anything other than Pending/Active.
        // (Task wording "Active以外" targets non-live statuses; Pending remains assignable until first desired.)
        if (devices.Count != idList.Count
            || devices.Any(d => d.Status is not (DeviceStatus.Active or DeviceStatus.Pending)))
        {
            return (400, new { error = "unknownOrRevokedDevice" });
        }

        foreach (var device in devices)
        {
            device.DesiredRevision += 1;
            device.RowVersion += 1;
        }

        var revByDevice = devices.ToDictionary(d => d.Id, d => d.DesiredRevision);
        foreach (var schedule in schedulesToStamp)
        {
            if (revByDevice.TryGetValue(schedule.DeviceId, out var rev))
            {
                schedule.ModifiedRevision = rev;
            }
        }

        if (tombstoneSchedules is not null)
        {
            foreach (var schedule in tombstoneSchedules)
            {
                if (!revByDevice.TryGetValue(schedule.DeviceId, out var rev))
                {
                    continue;
                }

                _db.Tombstones.Add(new DesiredTombstoneEntity
                {
                    Id = Guid.NewGuid(),
                    DeviceId = schedule.DeviceId,
                    ScheduleId = schedule.Id,
                    CreatedAtRevision = rev,
                    Cleared = false,
                });
            }
        }

        return null;
    }

    private static string EncodeDaysOrDate(AlarmGroupWriteRequest request)
    {
        if (string.Equals(request.Repeat, "once", StringComparison.OrdinalIgnoreCase))
        {
            return JsonUtil.Serialize(new { date = request.Date });
        }

        return JsonUtil.Serialize(request.Days ?? Array.Empty<int>());
    }

    private static AlarmGroupDto Map(AlarmGroupEntity g)
    {
        IReadOnlyList<int>? days = null;
        string? date = null;
        if (string.Equals(g.Repeat, "once", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(g.DaysOrDateJson);
            if (doc.RootElement.TryGetProperty("date", out var d))
            {
                date = d.GetString();
            }
        }
        else
        {
            days = JsonUtil.Deserialize<List<int>>(g.DaysOrDateJson);
        }

        var excluded = JsonUtil.Deserialize<List<string>>(g.ExcludedDatesJson);
        return new AlarmGroupDto(
            g.Id,
            g.Label,
            g.Enabled,
            g.Version,
            g.DeletedAt,
            g.Repeat,
            days,
            date,
            g.TimeZone,
            g.ExcludeHolidays,
            excluded,
            g.Targets.OrderBy(t => t.DeviceId).Select(t => new AlarmTargetDto(
                t.Id, t.DeviceId, t.Time, t.SoundKey, t.Volume, t.Vibrate, t.Enabled)).ToList());
    }

    private static bool IsSameContent(AlarmGroupDto existing, AlarmGroupWriteRequest request)
    {
        if (!string.Equals(existing.Label, request.Label, StringComparison.Ordinal)) return false;
        if (existing.Enabled != request.Enabled) return false;
        if (!string.Equals(existing.Repeat, request.Repeat, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(existing.TimeZone, request.TimeZone, StringComparison.Ordinal)) return false;
        if (existing.ExcludeHolidays != request.ExcludeHolidays) return false;
        var excl = request.ExcludedDates ?? Array.Empty<string>();
        if (!existing.ExcludedDates.SequenceEqual(excl)) return false;
        if (existing.Targets.Count != request.Targets.Count) return false;

        var a = existing.Targets.OrderBy(t => t.DeviceId).ToList();
        var b = request.Targets.OrderBy(t => t.DeviceId).ToList();
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].DeviceId != b[i].DeviceId) return false;
            if (a[i].Time != b[i].Time) return false;
            if (a[i].SoundKey != b[i].SoundKey) return false;
            if (a[i].Volume != b[i].Volume) return false;
            if (a[i].Vibrate != b[i].Vibrate) return false;
            if (a[i].Enabled != b[i].Enabled) return false;
        }

        if (string.Equals(existing.Repeat, "once", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(existing.Date, request.Date, StringComparison.Ordinal);
        }

        var daysA = existing.Days ?? Array.Empty<int>();
        var daysB = request.Days ?? Array.Empty<int>();
        return daysA.SequenceEqual(daysB);
    }
}
