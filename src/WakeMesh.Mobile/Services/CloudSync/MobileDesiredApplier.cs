using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Production implementation of <see cref="IDesiredApplier"/> for the Mobile client.
/// Reconciles server desired schedules into <see cref="LocalAlarmModel"/> via <see cref="AlarmCoordinator"/>
/// while strictly preserving local manual alarms using an isolated <see cref="CloudSyncManifest"/>.
/// Pure service — does not reference MAUI or UI types.
/// </summary>
/// <remarks>
/// Side-effect safety contract:
/// <list type="bullet">
/// <item>Every desired item is validated (time/date/days/repeat/duplicate ids) <b>before</b> the first
/// store, OS, or manifest mutation, so one malformed item cannot leave partial side effects.</item>
/// <item>Cloud-owned local ids are always <c>sync-{bindingKey}-{scheduleId:N}</c>
/// (<see cref="SyncLocalAlarmIdentity"/>); ownership is re-derivable without a committed manifest,
/// so a process death between the OS/store effects and the manifest commit still leaves the leftovers
/// collectible on the next cycle.</item>
/// <item>Deletions use <see cref="AlarmCoordinator.CancelThenDeleteAsync"/>: an OS cancel failure is a
/// reported failure (no success ACK, no applied-revision commit) and the store entry survives for retry.</item>
/// <item>A manifest commit failure is never reported as success.</item>
/// </list>
/// </remarks>
public sealed class MobileDesiredApplier : IDesiredApplier
{
    private readonly CloudSyncBinding _binding;
    private readonly AlarmCoordinator _coordinator;
    private readonly ICloudSyncManifestStore _manifestStore;

    public MobileDesiredApplier(
        CloudSyncBinding binding,
        AlarmCoordinator coordinator,
        ICloudSyncManifestStore manifestStore)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
    }

    public async Task<DesiredApplyResult> ApplyAsync(
        DesiredSnapshotDto snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        if (snapshot.DeviceId != _binding.ServerDeviceId)
        {
            return new DesiredApplyResult(
                Succeeded: false,
                Completed: false,
                Failures: new[]
                {
                    new AckFailureDto(
                        null,
                        "DeviceIdMismatch",
                        $"Snapshot deviceId {snapshot.DeviceId} does not match binding {_binding.ServerDeviceId}")
                });
        }

        // Phase 0 — validate the whole snapshot before any side effect. A malformed item must not leave
        // a partially applied state (no store write, no OS registration, no manifest change).
        var validationFailures = ValidateSnapshot(snapshot);
        if (validationFailures.Count > 0)
        {
            return new DesiredApplyResult(Succeeded: false, Completed: true, Failures: validationFailures);
        }

        // 1. Load manifest. Corrupted manifest throws CorruptedSyncManifestException (never silently empty).
        var currentManifest = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var workingManifest = currentManifest.Clone();

        // 2. Load all current local alarms
        var localAlarms = await _coordinator.GetAllAsync().ConfigureAwait(false);
        var localAlarmsById = localAlarms.ToDictionary(a => a.Id, StringComparer.Ordinal);

        var failures = new List<AckFailureDto>();
        var desiredIds = snapshot.Items.Select(i => i.ScheduleId).ToHashSet();
        var tombstones = snapshot.Tombstones.Select(t => t.ScheduleId).ToHashSet();

        // 3. Collect every cloud-owned local alarm that is no longer desired.
        //    Ownership has two independent sources so cleanup survives a lost/late manifest commit:
        //    (a) manifest mappings, (b) the deterministic binding prefix on the stored alarms.
        var deleteCandidates = new Dictionary<string, Guid?>(StringComparer.Ordinal);

        foreach (var entry in workingManifest.Schedules.Values)
        {
            if (tombstones.Contains(entry.ScheduleId) || !desiredIds.Contains(entry.ScheduleId))
            {
                deleteCandidates[entry.LocalAlarmId] = entry.ScheduleId;
            }
        }

        foreach (var alarm in localAlarms)
        {
            if (!SyncLocalAlarmIdentity.TryParseScheduleId(_binding, alarm.Id, out var scheduleId))
            {
                continue; // manual / foreign binding alarm: never touched
            }

            if (desiredIds.Contains(scheduleId))
            {
                continue;
            }

            deleteCandidates.TryAdd(alarm.Id, scheduleId);
        }

        foreach (var (localAlarmId, scheduleId) in deleteCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Strict: OS cancel must succeed before the store entry disappears.
                await _coordinator.CancelThenDeleteAsync(localAlarmId).ConfigureAwait(false);

                if (scheduleId is Guid knownScheduleId)
                {
                    workingManifest.Schedules.Remove(knownScheduleId);
                }

                localAlarmsById.Remove(localAlarmId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // OS still holds the schedule and the store entry is preserved, so the next cycle retries.
                failures.Add(new AckFailureDto(
                    scheduleId,
                    "AlarmCancelFailed",
                    ex.Message));
            }
        }

        // 4. Identify schedules to add or update
        foreach (var item in snapshot.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localAlarmId = ResolveLocalAlarmId(workingManifest, item.ScheduleId);

            if (!localAlarmsById.TryGetValue(localAlarmId, out var alarm))
            {
                alarm = new LocalAlarmModel
                {
                    Id = localAlarmId,
                    DeviceId = _binding.ServerDeviceId.ToString()
                };
            }

            // Map server item fields to LocalAlarmModel
            alarm.Name = item.Label;
            alarm.IsEnabled = item.GroupEnabled && item.Enabled;
            alarm.Time = ParseTime(item.Time);
            alarm.TimeZoneId = string.IsNullOrWhiteSpace(item.TimeZone) ? "Asia/Tokyo" : item.TimeZone;
            alarm.ExcludeHolidays = item.ExcludeHolidays;
            alarm.VolumePercent = Math.Clamp(item.Volume, 0, 100);
            alarm.EnableVibration = item.Vibrate;
            alarm.SoundUri = item.SoundKey;
            alarm.SyncStatus = AlarmSyncStatus.Synced;

            if (string.Equals(item.Repeat, "once", StringComparison.OrdinalIgnoreCase))
            {
                alarm.RepeatKind = AlarmRepeatKind.Once;
                alarm.OnceOnDate = !string.IsNullOrWhiteSpace(item.Date) ? DateOnly.Parse(item.Date, CultureInfo.InvariantCulture) : null;
                alarm.ActiveWeekdays = Weekdays.None;
            }
            else
            {
                alarm.RepeatKind = AlarmRepeatKind.Weekly;
                alarm.OnceOnDate = null;
                alarm.ActiveWeekdays = ConvertDaysToWeekdays(item.Days);
            }

            // Save and schedule via Coordinator (atomic store save + OS alarm registration)
            try
            {
                await _coordinator.SaveAndScheduleAsync(alarm).ConfigureAwait(false);

                if (alarm.OsRegistrationStatus == AlarmRegistrationStatus.Failed)
                {
                    // No manifest entry: the deterministic prefix still lets a later cycle collect it.
                    failures.Add(new AckFailureDto(
                        item.ScheduleId,
                        "OsRegistrationFailed",
                        alarm.OsRegistrationError ?? "Failed to schedule alarm in OS"));
                }
                else
                {
                    workingManifest.Schedules[item.ScheduleId] = new SyncScheduleEntry(
                        item.ScheduleId,
                        alarm.Id,
                        item.GroupId,
                        item.ModifiedRevision);
                    localAlarmsById[alarm.Id] = alarm;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new AckFailureDto(
                    item.ScheduleId,
                    "ApplyException",
                    ex.Message));
            }
        }

        // 5. Commit the manifest. A commit failure is never reported as success: the already applied
        //    OS/store effects remain recoverable through the deterministic prefix on the next cycle.
        try
        {
            await _manifestStore.SaveAsync(workingManifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(new AckFailureDto(
                null,
                "ManifestSaveFailed",
                ex.Message));
        }

        if (failures.Count > 0)
        {
            return new DesiredApplyResult(
                Succeeded: false,
                Completed: true,
                Failures: failures);
        }

        return new DesiredApplyResult(
            Succeeded: true,
            Completed: true,
            Failures: Array.Empty<AckFailureDto>());
    }

    /// <summary>
    /// Deterministic id for new schedules; an existing manifest mapping is preserved so upgrades from
    /// earlier builds keep their already-created local alarms.
    /// </summary>
    private string ResolveLocalAlarmId(CloudSyncManifest manifest, Guid scheduleId)
    {
        if (manifest.Schedules.TryGetValue(scheduleId, out var entry) &&
            !string.IsNullOrWhiteSpace(entry.LocalAlarmId))
        {
            return entry.LocalAlarmId;
        }

        return SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId);
    }

    private static List<AckFailureDto> ValidateSnapshot(DesiredSnapshotDto snapshot)
    {
        var failures = new List<AckFailureDto>();
        var seen = new HashSet<Guid>();

        foreach (var item in snapshot.Items)
        {
            if (item is null)
            {
                failures.Add(new AckFailureDto(null, "InvalidItem", "null item"));
                continue;
            }

            if (item.ScheduleId == Guid.Empty)
            {
                failures.Add(new AckFailureDto(null, "InvalidItem", "empty scheduleId"));
                continue;
            }

            if (!seen.Add(item.ScheduleId))
            {
                failures.Add(new AckFailureDto(item.ScheduleId, "DuplicateSchedule", "duplicate scheduleId in desired snapshot"));
                continue;
            }

            if (!TimeOnly.TryParse(item.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                failures.Add(new AckFailureDto(item.ScheduleId, "InvalidItem", "invalid time"));
                continue;
            }

            var isOnce = string.Equals(item.Repeat, "once", StringComparison.OrdinalIgnoreCase);
            var isWeekly = string.Equals(item.Repeat, "weekly", StringComparison.OrdinalIgnoreCase);
            if (!isOnce && !isWeekly)
            {
                failures.Add(new AckFailureDto(item.ScheduleId, "InvalidItem", "invalid repeat"));
                continue;
            }

            if (isOnce)
            {
                if (!string.IsNullOrWhiteSpace(item.Date) &&
                    (!DateOnly.TryParse(item.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var onceDate) ||
                     onceDate == default))
                {
                    failures.Add(new AckFailureDto(item.ScheduleId, "InvalidItem", "invalid date"));
                    continue;
                }
            }
            else
            {
                // Weekly requires a non-empty days list. Null/empty must fail closed in Phase 0 so a
                // sibling valid item and any delete candidates cannot mutate store/OS/manifest.
                // (API may still accept empty days today — applier defense only; API fix is separate.)
                if (item.Days is null || item.Days.Count == 0)
                {
                    failures.Add(new AckFailureDto(item.ScheduleId, "InvalidItem", "empty days"));
                    continue;
                }

                // Wire contract (API/Client): 0=Sunday .. 6=Saturday (.NET DayOfWeek). 7 is invalid.
                var invalidDay = item.Days.Any(d => d is < 0 or > 6);
                if (invalidDay)
                {
                    failures.Add(new AckFailureDto(item.ScheduleId, "InvalidItem", "invalid days"));
                    continue;
                }
            }
        }

        foreach (var tombstone in snapshot.Tombstones)
        {
            if (tombstone is null || tombstone.ScheduleId == Guid.Empty)
            {
                failures.Add(new AckFailureDto(null, "InvalidItem", "invalid tombstone"));
            }
        }

        return failures;
    }

    private static TimeOnly ParseTime(string timeStr)
    {
        if (TimeOnly.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        throw new FormatException($"Invalid time string format: '{timeStr}'");
    }

    private static Weekdays ConvertDaysToWeekdays(IReadOnlyList<int>? days)
    {
        if (days is null || days.Count == 0)
        {
            return Weekdays.None;
        }

        var result = Weekdays.None;
        foreach (var day in days)
        {
            // API/Client wire: 0=Sun..6=Sat. Core Weekdays bitflags remain Monday=1<<0 .. Sunday=1<<6.
            result |= day switch
            {
                0 => Weekdays.Sunday,
                1 => Weekdays.Monday,
                2 => Weekdays.Tuesday,
                3 => Weekdays.Wednesday,
                4 => Weekdays.Thursday,
                5 => Weekdays.Friday,
                6 => Weekdays.Saturday,
                _ => Weekdays.None
            };
        }

        return result;
    }
}
