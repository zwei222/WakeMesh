using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Result of one projection: the alarms that stay in the existing (manual) list, and the additional
/// read-only group rows. <see cref="LocalAlarms"/> and <see cref="GroupAlarms"/> are mutually exclusive
/// per stored alarm id (T113 §4 step 5).
/// </summary>
public sealed record GroupAlarmProjection(
    IReadOnlyList<LocalAlarmModel> LocalAlarms,
    IReadOnlyList<GroupAlarmRow> GroupAlarms);

/// <summary>
/// Pure classifier for the alarm list (T113 §2/§4). It reads the stored alarms, the current binding's
/// manifest and the last observation — nothing else — and never writes, reserves or schedules anything.
/// Defensive by construction: a corrupt manifest, a malformed snapshot or an unknown registration only
/// removes information, never stored rows, and never throws.
/// </summary>
public static class GroupAlarmProjector
{
    public const string PendingWaitingText = "この端末への反映待ち";
    public const string PendingApplyFailedText = "この端末への反映に失敗しました（次の同期で再試行します）";
    public const string NewSettingsPendingText = "新しい設定の反映待ちです（反映までは表示中の設定で鳴ります）";
    public const string DeletePendingText = "削除の反映待ちです（反映までは鳴ります）";
    public const string OrphanWarningText = "以前のクラウド登録で設定されたアラームです。クラウドの画面で同期の状態を確認してください。";
    public const string ManifestUnreadableWarningText = "グループとの対応を確認できません";
    public const string RegistrationFailedText = "端末にアラームを設定できていません";
    public const string EnabledTextOn = "オン";
    public const string EnabledTextOff = "オフ";
    public const string PendingEnabledText = "反映待ち";
    public const string RowKeyCloudPrefix = "cloud:";
    public const string RowKeyPendingPrefix = "pending:";
    public const string RowKeyOrphanPrefix = "orphan:";

    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly IReadOnlyDictionary<Guid, DesiredItemDto> NoItems =
        new Dictionary<Guid, DesiredItemDto>();

    public static GroupAlarmProjection Project(
        IReadOnlyList<LocalAlarmModel>? stored,
        CloudSyncBinding? binding,
        CloudSyncManifest? manifest,
        DesiredObservation? observed,
        TimeZoneInfo? deviceTimeZone,
        bool manifestUnreadable = false)
    {
        var storedAlarms = stored ?? Array.Empty<LocalAlarmModel>();
        var timeZoneId = deviceTimeZone?.Id;
        var manifestIndex = BuildManifestIndex(manifest);
        var sameBinding = ResolveSameBinding(observed, binding);

        var desiredItems = sameBinding is null
            ? NoItems
            : IndexDesiredItems(sameBinding.Snapshot);
        var tombstones = sameBinding is null
            ? new HashSet<Guid>()
            : ReadTombstones(sameBinding.Snapshot);

        var local = new List<LocalAlarmModel>();
        var rows = new List<GroupAlarmRow>();
        var nextFireByRowKey = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        var cloudLocalIds = new HashSet<string>(StringComparer.Ordinal);
        var storedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var alarm in storedAlarms)
        {
            if (alarm is null || string.IsNullOrEmpty(alarm.Id) || !storedIds.Add(alarm.Id))
            {
                continue;   // malformed / duplicate stored row: keep the first one, never crash
            }

            if (manifestIndex.TryGetValue(alarm.Id, out var entry))
            {
                var row = BuildAppliedRow(
                    alarm,
                    entry.GroupId,
                    entry.ScheduleId,
                    entry.ModifiedRevision,
                    desiredItems,
                    tombstones,
                    sameBinding is not null,
                    timeZoneId,
                    manifestUnreadable);

                AddRow(row, alarm.NextFireTime);
                cloudLocalIds.Add(alarm.Id);
                continue;
            }

            if (binding is not null &&
                SyncLocalAlarmIdentity.TryParseScheduleId(binding, alarm.Id, out var parsedScheduleId))
            {
                desiredItems.TryGetValue(parsedScheduleId, out var desiredItem);
                var row = BuildAppliedRow(
                    alarm,
                    desiredItem?.GroupId,
                    parsedScheduleId,
                    modifiedRevision: null,
                    desiredItems,
                    tombstones,
                    sameBinding is not null,
                    timeZoneId,
                    manifestUnreadable);

                AddRow(row, alarm.NextFireTime);
                cloudLocalIds.Add(alarm.Id);
                continue;
            }

            if (alarm.Id.StartsWith(SyncLocalAlarmIdentity.IdPrefix, StringComparison.Ordinal))
            {
                AddRow(BuildOrphanRow(alarm, timeZoneId, manifestUnreadable), alarm.NextFireTime);
                cloudLocalIds.Add(alarm.Id);
                continue;
            }

            local.Add(alarm);
        }

        // Pending rows: in the last really-received desired snapshot of this binding, but nothing for them
        // is stored on this device yet, so they cannot ring.
        foreach (var item in desiredItems.Values.OrderBy(i => i.ScheduleId))
        {
            var localId = ResolveLocalAlarmId(manifest, binding, item.ScheduleId);
            if (cloudLocalIds.Contains(localId))
            {
                continue;
            }

            AddRow(BuildPendingRow(item, sameBinding!.Status, timeZoneId), null);
        }

        var ordered = rows
            .Where(row => row.Origin != GroupAlarmOrigin.CloudPending)
            .OrderBy(row => nextFireByRowKey.TryGetValue(row.RowKey, out var nextFire) && nextFire.HasValue ? 0 : 1)
            .ThenBy(row => nextFireByRowKey.TryGetValue(row.RowKey, out var nextFire2) ? nextFire2 ?? DateTimeOffset.MaxValue : DateTimeOffset.MaxValue)
            .ThenBy(row => row.GroupName, StringComparer.Ordinal)
            .ThenBy(row => row.RowKey, StringComparer.Ordinal)
            .Concat(rows
                .Where(row => row.Origin == GroupAlarmOrigin.CloudPending)
                .OrderBy(row => row.TimeText, StringComparer.Ordinal)
                .ThenBy(row => row.GroupName, StringComparer.Ordinal)
                .ThenBy(row => row.RowKey, StringComparer.Ordinal))
            .ToList();

        return new GroupAlarmProjection(local, ordered);

        void AddRow(GroupAlarmRow row, DateTimeOffset? nextFireTime)
        {
            if (!emittedKeys.Add(row.RowKey))
            {
                return;   // duplicate RowKey: one row only
            }

            rows.Add(row);
            nextFireByRowKey[row.RowKey] = nextFireTime;
        }
    }

    /// <summary>True when the id belongs to the cloud scope (current binding prefix or a manifest mapping).</summary>
    public static bool IsCloudManagedId(
        string? localAlarmId,
        CloudSyncBinding? binding,
        CloudSyncManifest? manifest)
    {
        if (string.IsNullOrEmpty(localAlarmId))
        {
            return false;
        }

        if (localAlarmId.StartsWith(SyncLocalAlarmIdentity.IdPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (binding is not null && SyncLocalAlarmIdentity.TryParseScheduleId(binding, localAlarmId, out _))
        {
            return true;
        }

        return BuildManifestIndex(manifest).ContainsKey(localAlarmId);
    }

    private static Dictionary<string, SyncScheduleEntry> BuildManifestIndex(CloudSyncManifest? manifest)
    {
        var index = new Dictionary<string, SyncScheduleEntry>(StringComparer.Ordinal);
        var schedules = manifest?.Schedules;
        if (schedules is null)
        {
            return index;
        }

        foreach (var entry in schedules.Values
                     .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.LocalAlarmId))
                     .OrderBy(entry => entry.ScheduleId))
        {
            index.TryAdd(entry.LocalAlarmId, entry);   // duplicate ids: the smallest schedule id wins
        }

        return index;
    }

    private static DesiredObservation? ResolveSameBinding(DesiredObservation? observed, CloudSyncBinding? binding)
    {
        if (observed is null || binding is null || observed.Snapshot is null)
        {
            return null;
        }

        return string.Equals(observed.BindingKey, binding.ToStorageKey(), StringComparison.Ordinal)
            ? observed
            : null;
    }

    private static IReadOnlyDictionary<Guid, DesiredItemDto> IndexDesiredItems(DesiredSnapshotDto snapshot)
    {
        var items = snapshot.Items;
        if (items is null)
        {
            return NoItems;
        }

        var indexed = new Dictionary<Guid, DesiredItemDto>();
        foreach (var item in items)
        {
            if (item is null || item.ScheduleId == Guid.Empty)
            {
                continue;
            }

            indexed.TryAdd(item.ScheduleId, item);
        }

        return indexed;
    }

    private static HashSet<Guid> ReadTombstones(DesiredSnapshotDto snapshot)
    {
        var tombstones = new HashSet<Guid>();
        var entries = snapshot.Tombstones;
        if (entries is null)
        {
            return tombstones;
        }

        foreach (var tombstone in entries)
        {
            if (tombstone is not null && tombstone.ScheduleId != Guid.Empty)
            {
                tombstones.Add(tombstone.ScheduleId);
            }
        }

        return tombstones;
    }

    /// <summary>Same order the applier uses: a committed manifest mapping wins, otherwise the deterministic id.</summary>
    private static string ResolveLocalAlarmId(CloudSyncManifest? manifest, CloudSyncBinding? binding, Guid scheduleId)
    {
        if (manifest?.Schedules is not null &&
            manifest.Schedules.TryGetValue(scheduleId, out var entry) &&
            !string.IsNullOrWhiteSpace(entry?.LocalAlarmId))
        {
            return entry!.LocalAlarmId;
        }

        return binding is null ? string.Empty : SyncLocalAlarmIdentity.GetLocalAlarmId(binding, scheduleId);
    }

    private static GroupAlarmRow BuildAppliedRow(
        LocalAlarmModel alarm,
        string? groupId,
        Guid scheduleId,
        long? modifiedRevision,
        IReadOnlyDictionary<Guid, DesiredItemDto> desiredItems,
        HashSet<Guid> tombstones,
        bool hasObservation,
        string? deviceTimeZoneId,
        bool manifestUnreadable)
    {
        var resolvedGroupId = string.IsNullOrWhiteSpace(groupId) ? null : groupId;
        var pendingText = (string?)null;

        if (hasObservation)
        {
            if (desiredItems.TryGetValue(scheduleId, out var desiredItem))
            {
                if (modifiedRevision.HasValue && modifiedRevision.Value < desiredItem.ModifiedRevision)
                {
                    pendingText = NewSettingsPendingText;
                }
            }
            else
            {
                pendingText = DeletePendingText;
            }
        }

        if (pendingText == DeletePendingText && tombstones.Contains(scheduleId))
        {
            pendingText = DeletePendingText;   // explicit tombstone: same wording, row stays because it still rings
        }

        var warnings = new List<string>();
        if (alarm.IsRegistrationFailed)
        {
            warnings.Add(RegistrationFailedText);
        }

        if (manifestUnreadable)
        {
            warnings.Add(ManifestUnreadableWarningText);
        }

        return new GroupAlarmRow(
            RowKey: $"{RowKeyCloudPrefix}{alarm.Id}",
            Origin: GroupAlarmOrigin.CloudApplied,
            LocalAlarmId: alarm.Id,
            ScheduleId: scheduleId == Guid.Empty ? null : scheduleId,
            GroupId: resolvedGroupId,
            GroupName: NameOf(alarm.Name),
            TimeText: FormatTime(alarm.Time),
            DaysText: alarm.DaysSummary,
            NextFireText: NextFireText(alarm),
            IsEnabled: alarm.IsEnabled,
            EnabledText: alarm.IsEnabled ? EnabledTextOn : EnabledTextOff,
            TimeZoneNote: TimeZoneNote(alarm.TimeZoneId, deviceTimeZoneId),
            SkipHolidays: alarm.ExcludeHolidays,
            HasHolidayCoverageWarning: alarm.HasHolidayCoverageWarning,
            IsRegistrationFailed: alarm.IsRegistrationFailed,
            PendingText: pendingText,
            WarningText: warnings.Count == 0 ? null : string.Join("・", warnings),
            CanOpenGroup: resolvedGroupId is not null);
    }

    private static GroupAlarmRow BuildOrphanRow(LocalAlarmModel alarm, string? deviceTimeZoneId, bool manifestUnreadable)
    {
        var warnings = new List<string> { OrphanWarningText };
        if (alarm.IsRegistrationFailed)
        {
            warnings.Add(RegistrationFailedText);
        }

        if (manifestUnreadable)
        {
            warnings.Add(ManifestUnreadableWarningText);
        }

        return new GroupAlarmRow(
            RowKey: $"{RowKeyOrphanPrefix}{alarm.Id}",
            Origin: GroupAlarmOrigin.CloudOrphan,
            LocalAlarmId: alarm.Id,
            ScheduleId: null,
            GroupId: null,
            GroupName: NameOf(alarm.Name),
            TimeText: FormatTime(alarm.Time),
            DaysText: alarm.DaysSummary,
            NextFireText: NextFireText(alarm),
            IsEnabled: alarm.IsEnabled,
            EnabledText: alarm.IsEnabled ? EnabledTextOn : EnabledTextOff,
            TimeZoneNote: TimeZoneNote(alarm.TimeZoneId, deviceTimeZoneId),
            SkipHolidays: alarm.ExcludeHolidays,
            HasHolidayCoverageWarning: alarm.HasHolidayCoverageWarning,
            IsRegistrationFailed: alarm.IsRegistrationFailed,
            PendingText: null,
            WarningText: string.Join("・", warnings),
            CanOpenGroup: false);
    }

    private static GroupAlarmRow BuildPendingRow(DesiredItemDto item, DesiredSyncStatus status, string? deviceTimeZoneId)
    {
        var model = ToDisplayModel(item);
        var groupId = string.IsNullOrWhiteSpace(item.GroupId) ? null : item.GroupId;

        return new GroupAlarmRow(
            RowKey: $"{RowKeyPendingPrefix}{item.ScheduleId:N}",
            Origin: GroupAlarmOrigin.CloudPending,
            LocalAlarmId: null,
            ScheduleId: item.ScheduleId,
            GroupId: groupId,
            GroupName: NameOf(item.Label),
            TimeText: FormatTime(model.Time),
            DaysText: model.DaysSummary,
            NextFireText: null,   // nothing is scheduled on this device yet
            IsEnabled: item.GroupEnabled && item.Enabled,
            EnabledText: PendingEnabledText,
            TimeZoneNote: TimeZoneNote(model.TimeZoneId, deviceTimeZoneId),
            SkipHolidays: item.ExcludeHolidays,
            HasHolidayCoverageWarning: false,
            IsRegistrationFailed: false,
            PendingText: status == DesiredSyncStatus.ApplyFailedAcknowledged
                ? PendingApplyFailedText
                : PendingWaitingText,
            WarningText: null,
            CanOpenGroup: groupId is not null);
    }

    /// <summary>Temporary display model so Pending rows share the exact day wording of a stored alarm.</summary>
    private static LocalAlarmModel ToDisplayModel(DesiredItemDto item)
    {
        var model = new LocalAlarmModel
        {
            Name = item.Label ?? string.Empty,
            TimeZoneId = string.IsNullOrWhiteSpace(item.TimeZone) ? "Asia/Tokyo" : item.TimeZone
        };

        if (TimeOnly.TryParse(item.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            model.Time = time;
        }

        if (string.Equals(item.Repeat, "once", StringComparison.OrdinalIgnoreCase))
        {
            model.RepeatKind = AlarmRepeatKind.Once;
            model.ActiveWeekdays = Weekdays.None;
            if (!string.IsNullOrWhiteSpace(item.Date) &&
                DateOnly.TryParse(item.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                date != default)
            {
                model.OnceOnDate = date;
            }
        }
        else
        {
            model.RepeatKind = AlarmRepeatKind.Weekly;
            model.ActiveWeekdays = ConvertDaysToWeekdays(item.Days);
        }

        return model;
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

    private static string NameOf(string? name) =>
        string.IsNullOrWhiteSpace(name) ? GroupAlarmRow.UnnamedGroupName : name.Trim();

    private static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? NextFireText(LocalAlarmModel alarm) =>
        alarm.IsEnabled && alarm.NextFireTime.HasValue
            ? $"次回 {alarm.NextFireTime.Value.ToLocalTime().ToString("M月d日(ddd) H:mm", Japanese)}"
            : null;

    private static string? TimeZoneNote(string? alarmTimeZoneId, string? deviceTimeZoneId) =>
        !string.IsNullOrWhiteSpace(alarmTimeZoneId) &&
        !string.IsNullOrWhiteSpace(deviceTimeZoneId) &&
        !string.Equals(alarmTimeZoneId, deviceTimeZoneId, StringComparison.Ordinal)
            ? $"{alarmTimeZoneId} の時刻"
            : null;
}
