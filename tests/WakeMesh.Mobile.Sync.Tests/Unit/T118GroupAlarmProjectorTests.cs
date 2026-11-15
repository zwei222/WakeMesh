using System;
using System.Collections.Generic;
using System.Linq;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services.CloudSync;
using Xunit;

namespace WakeMesh.Mobile.Sync.Tests.Unit;

/// <summary>
/// T118: the pure classifier behind the "group alarms" section (T113 §2/§4) and the in-memory observation
/// hand-over (T113 §5). These tests run the real types with the real wire DTOs; nothing here touches MAUI,
/// the store or the OS scheduler.
/// </summary>
public sealed class T118GroupAlarmProjectorTests
{
    private static readonly Guid DeviceId = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly Guid ScheduleA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ScheduleB = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static CloudSyncBinding Binding(long generation = 1) =>
        new("https://api.wakemesh.local", "owner-account", DeviceId, generation);

    private static LocalAlarmModel Stored(
        string id,
        string name = "グループ朝",
        bool enabled = true,
        string timeZone = "Asia/Tokyo",
        bool registrationFailed = false,
        DateTimeOffset? nextFire = null) => new()
        {
            Id = id,
            Name = name,
            IsEnabled = enabled,
            Time = new TimeOnly(7, 0),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
            DeviceId = DeviceId.ToString(),
            TimeZoneId = timeZone,
            SyncStatus = AlarmSyncStatus.Synced,
            OsRegistrationStatus = registrationFailed ? AlarmRegistrationStatus.Failed : AlarmRegistrationStatus.Scheduled,
            NextFireTime = enabled ? nextFire : null
        };

    private static CloudSyncManifest Manifest(params SyncScheduleEntry[] entries)
    {
        var manifest = new CloudSyncManifest
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AccountId = "owner-account",
            ServerDeviceId = DeviceId,
            RegistrationGeneration = 1
        };

        foreach (var entry in entries)
        {
            manifest.Schedules[entry.ScheduleId] = entry;
        }

        return manifest;
    }

    private static DesiredItemDto Item(
        Guid scheduleId,
        string groupId = "group-1",
        string label = "グループ朝",
        long modifiedRevision = 10,
        bool enabled = true,
        bool groupEnabled = true,
        string time = "07:00",
        IReadOnlyList<int>? days = null) =>
        new(
            scheduleId,
            groupId,
            label,
            groupEnabled,
            "weekly",
            days ?? new[] { 1, 2, 3, 4, 5 },
            null,
            "Asia/Tokyo",
            true,
            Array.Empty<string>(),
            time,
            "sound-default",
            80,
            true,
            enabled,
            modifiedRevision);

    private static DesiredSnapshotDto Snapshot(
        IEnumerable<DesiredItemDto>? items = null,
        IEnumerable<DesiredTombstoneDto>? tombstones = null,
        long revision = 5) =>
        new(DeviceId, revision, Guid.NewGuid(), items?.ToList() ?? new List<DesiredItemDto>(),
            tombstones?.ToList() ?? new List<DesiredTombstoneDto>());

    private static DesiredObservation Observation(
        CloudSyncBinding binding,
        DesiredSnapshotDto snapshot,
        DesiredSyncStatus status = DesiredSyncStatus.AppliedAcknowledged) =>
        new(binding.ToStorageKey(), snapshot, status, DateTimeOffset.UtcNow);

    private static TimeZoneInfo Tokyo => TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // --- 1) manual alarms keep the list exactly as it was -------------------

    [Fact]
    public void ManualOnly_StaysLocalAndProducesNoGroupRows()
    {
        var result = GroupAlarmProjector.Project(
            new[] { Stored("manual-1", "手動アラーム") },
            binding: null,
            manifest: null,
            observed: null,
            Tokyo);

        Assert.Single(result.LocalAlarms);
        Assert.Empty(result.GroupAlarms);
    }

    [Fact]
    public void NullInputs_DoNotThrowAndHideNothing()
    {
        var result = GroupAlarmProjector.Project(
            new[] { Stored("manual-1") },
            Binding(),
            manifest: null,
            observed: null,
            deviceTimeZone: null);

        Assert.Single(result.LocalAlarms);
        Assert.Empty(result.GroupAlarms);
    }

    // --- 2) applied rows come from the real manifest ------------------------

    [Fact]
    public void ManifestMappedAlarm_IsAppliedGroupRow_AndLeavesTheManualList()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 10));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId, "グループ朝", nextFire: DateTimeOffset.UtcNow.AddHours(3)) },
            binding,
            manifest,
            observed: null,
            Tokyo);

        Assert.Empty(result.LocalAlarms);
        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Equal("group-1", row.GroupId);
        Assert.Equal("グループ朝", row.GroupName);
        Assert.Equal("07:00", row.TimeText);
        Assert.Equal("平日 (月〜金)", row.DaysText);
        Assert.NotNull(row.NextFireText);
        Assert.StartsWith("次回 ", row.NextFireText);
        Assert.Equal("オン", row.EnabledText);
        Assert.True(row.CanOpenGroup);
        Assert.Equal($"cloud:{storedId}", row.RowKey);
        Assert.Null(row.PendingText);
    }

    [Fact]
    public void LegacyNonPrefixedManifestId_IsAppliedAndNeverManual()
    {
        var binding = Binding();
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, "legacy-alarm-id", "group-legacy", 3));

        var result = GroupAlarmProjector.Project(
            new[] { Stored("legacy-alarm-id") },
            binding,
            manifest,
            observed: null,
            Tokyo);

        Assert.Empty(result.LocalAlarms);
        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Equal("legacy-alarm-id", row.LocalAlarmId);
        Assert.Equal("group-legacy", row.GroupId);

        Assert.True(GroupAlarmProjector.IsCloudManagedId("legacy-alarm-id", binding, manifest));
        Assert.False(GroupAlarmProjector.IsCloudManagedId("manual-1", binding, manifest));
    }

    [Fact]
    public void PrefixOwnedAlarmWithoutManifest_IsAppliedAndTakesTheGroupIdFromTheObservation()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var observed = Observation(binding, Snapshot(new[] { Item(ScheduleA) }));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId) },
            binding,
            manifest: null,
            observed,
            Tokyo);

        Assert.Empty(result.LocalAlarms);
        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Equal("group-1", row.GroupId);
        Assert.Null(row.PendingText);
    }

    // --- 3) orphans --------------------------------------------------------

    [Fact]
    public void ForeignBindingPrefix_IsOrphanWithWarningAndNoGroupAction()
    {
        var oldBinding = Binding(generation: 7);
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(oldBinding, ScheduleA);

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId, nextFire: DateTimeOffset.UtcNow.AddHours(1)) },
            Binding(),
            manifest: null,
            observed: null,
            Tokyo);

        Assert.Empty(result.LocalAlarms);
        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudOrphan, row.Origin);
        Assert.False(row.CanOpenGroup);
        Assert.Null(row.GroupId);
        Assert.Contains(GroupAlarmProjector.OrphanWarningText, row.WarningText);
        Assert.NotNull(row.NextFireText);   // it can still ring, and the row says so
    }

    // --- 4) pending rows ---------------------------------------------------

    [Fact]
    public void DesiredItemWithoutStoredAlarm_IsPendingAndCannotRing()
    {
        var binding = Binding();
        var observed = Observation(binding, Snapshot(new[] { Item(ScheduleA, time: "06:30") }));

        var result = GroupAlarmProjector.Project(
            Array.Empty<LocalAlarmModel>(),
            binding,
            manifest: null,
            observed,
            Tokyo);

        Assert.Empty(result.LocalAlarms);
        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudPending, row.Origin);
        Assert.Equal(GroupAlarmProjector.PendingWaitingText, row.PendingText);
        Assert.Equal(GroupAlarmProjector.PendingEnabledText, row.EnabledText);
        Assert.Null(row.NextFireText);
        Assert.Null(row.LocalAlarmId);
        Assert.Equal($"pending:{ScheduleA:N}", row.RowKey);
        Assert.Equal("06:30", row.TimeText);
        Assert.True(row.CanOpenGroup);
    }

    [Fact]
    public void ApplyFailedObservation_MarksThePendingRowAsFailed()
    {
        var binding = Binding();
        var observed = Observation(
            binding,
            Snapshot(new[] { Item(ScheduleA) }),
            DesiredSyncStatus.ApplyFailedAcknowledged);

        var result = GroupAlarmProjector.Project(
            Array.Empty<LocalAlarmModel>(), binding, null, observed, Tokyo);

        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmProjector.PendingApplyFailedText, row.PendingText);
    }

    [Fact]
    public void AppliedRowBehindTheDesiredRevision_SaysTheNewSettingIsPending()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 4));
        var observed = Observation(binding, Snapshot(new[] { Item(ScheduleA, modifiedRevision: 9) }));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId) }, binding, manifest, observed, Tokyo);

        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Equal(GroupAlarmProjector.NewSettingsPendingText, row.PendingText);
    }

    [Fact]
    public void AppliedRowMissingFromDesired_OrTombstoned_SaysDeletionIsPending_AndStays()
    {
        var binding = Binding();
        var storedA = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var storedB = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleB);
        var manifest = Manifest(
            new SyncScheduleEntry(ScheduleA, storedA, "group-1", 4),
            new SyncScheduleEntry(ScheduleB, storedB, "group-2", 4));
        var observed = Observation(
            binding,
            Snapshot(items: Array.Empty<DesiredItemDto>(),
                tombstones: new[] { new DesiredTombstoneDto(ScheduleB) }));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedA), Stored(storedB, "もう一つ") },
            binding,
            manifest,
            observed,
            Tokyo);

        Assert.Equal(2, result.GroupAlarms.Count);
        Assert.All(result.GroupAlarms, row => Assert.Equal(GroupAlarmProjector.DeletePendingText, row.PendingText));
        Assert.All(result.GroupAlarms, row => Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin));
    }

    [Fact]
    public void ObservationOfAnotherBinding_CreatesNoPendingRow_AndKeepsTheAppliedRows()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 10));
        var foreignObservation = Observation(
            Binding(generation: 3),
            Snapshot(new[] { Item(ScheduleB) }));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId) }, binding, manifest, foreignObservation, Tokyo);

        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Null(row.PendingText);
    }

    // --- 5) corrupt / malformed inputs degrade, never hide or throw ---------

    [Fact]
    public void CorruptManifestFlag_KeepsPrefixRows_WithoutGroupLinkAndWithWarning()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId) },
            binding,
            manifest: null,
            observed: null,
            Tokyo,
            manifestUnreadable: true);

        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Null(row.GroupId);
        Assert.False(row.CanOpenGroup);
        Assert.Contains(GroupAlarmProjector.ManifestUnreadableWarningText, row.WarningText);
    }

    [Fact]
    public void DuplicateManifestLocalId_ProducesOneRowOnly()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(
            new SyncScheduleEntry(ScheduleB, storedId, "group-later", 4),
            new SyncScheduleEntry(ScheduleA, storedId, "group-earlier", 4));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId) }, binding, manifest, null, Tokyo);

        var row = Assert.Single(result.GroupAlarms);
        Assert.Equal("group-earlier", row.GroupId);   // smallest schedule id wins
    }

    [Fact]
    public void MalformedSnapshot_DoesNotThrowAndKeepsStoredRows()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 10));
        var brokenItems = new List<DesiredItemDto> { null!, Item(ScheduleA, time: "not-a-time") };
        var snapshot = new DesiredSnapshotDto(DeviceId, 2, Guid.NewGuid(), brokenItems, new List<DesiredTombstoneDto> { null! });
        var observed = Observation(binding, snapshot);

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId), Stored("manual-1", "手動") },
            binding,
            manifest,
            observed,
            Tokyo);

        Assert.Single(result.LocalAlarms);           // the manual alarm is still listed
        Assert.Equal("manual-1", result.LocalAlarms[0].Id);
        Assert.Single(result.GroupAlarms);           // the applied row is still listed
        Assert.Equal(GroupAlarmOrigin.CloudApplied, result.GroupAlarms[0].Origin);
    }

    [Fact]
    public void BadManifestEntries_AreIgnored()
    {
        var binding = Binding();
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, "  ", "group-1", 1));

        var result = GroupAlarmProjector.Project(
            new[] { Stored("manual-1") }, binding, manifest, null, Tokyo);

        Assert.Single(result.LocalAlarms);
        Assert.Empty(result.GroupAlarms);
    }

    // --- 6) presentation details -------------------------------------------

    [Fact]
    public void Ordering_IsNextFireThenPendingByTime()
    {
        var binding = Binding();
        var storedA = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var storedB = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleB);
        var manifest = Manifest(
            new SyncScheduleEntry(ScheduleA, storedA, "group-a", 10),
            new SyncScheduleEntry(ScheduleB, storedB, "group-b", 10));
        var later = DateTimeOffset.UtcNow.AddHours(9);
        var earlier = DateTimeOffset.UtcNow.AddHours(1);
        var observed = Observation(binding, Snapshot(new[] { Item(Guid.NewGuid(), time: "05:00") }));

        var result = GroupAlarmProjector.Project(
            new[]
            {
                Stored(storedA, "B グループ", nextFire: later),
                Stored(storedB, "A グループ", nextFire: earlier)
            },
            binding,
            manifest,
            observed,
            Tokyo);

        Assert.Equal(3, result.GroupAlarms.Count);
        Assert.Equal("cloud:" + storedB, result.GroupAlarms[0].RowKey);
        Assert.Equal("cloud:" + storedA, result.GroupAlarms[1].RowKey);
        Assert.Equal(GroupAlarmOrigin.CloudPending, result.GroupAlarms[2].Origin);
    }

    [Fact]
    public void ForeignTimeZone_GetsANote()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 10));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId, timeZone: "America/New_York") },
            binding,
            manifest,
            null,
            Tokyo);

        Assert.Equal("America/New_York の時刻", Assert.Single(result.GroupAlarms).TimeZoneNote);
    }

    [Fact]
    public void EmptyGroupName_FallsBackToTheSharedWording()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 10));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId, name: "  ") }, binding, manifest, null, Tokyo);

        Assert.Equal(GroupAlarmRow.UnnamedGroupName, Assert.Single(result.GroupAlarms).GroupName);
    }

    [Fact]
    public void RegistrationFailure_IsCarriedToTheRow()
    {
        var binding = Binding();
        var storedId = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, ScheduleA);
        var manifest = Manifest(new SyncScheduleEntry(ScheduleA, storedId, "group-1", 10));

        var result = GroupAlarmProjector.Project(
            new[] { Stored(storedId, registrationFailed: true) },
            binding,
            manifest,
            null,
            Tokyo);

        var row = Assert.Single(result.GroupAlarms);
        Assert.True(row.IsRegistrationFailed);
        Assert.Contains(GroupAlarmProjector.RegistrationFailedText, row.WarningText);
    }

    // --- 7) the observation hand-over --------------------------------------

    [Fact]
    public void ObservationStore_PublishesTheLatestAndClears()
    {
        var store = new DesiredObservationStore();
        var notified = 0;
        store.Changed += (_, _) => notified++;

        Assert.Null(store.Latest);

        store.Publish("key-1", Snapshot(), DesiredSyncStatus.AppliedAcknowledged, DateTimeOffset.UtcNow);
        Assert.Equal("key-1", store.Latest!.BindingKey);
        Assert.Equal(1, notified);

        store.Publish("key-2", Snapshot(), DesiredSyncStatus.ApplyFailedAcknowledged, DateTimeOffset.UtcNow);
        Assert.Equal("key-2", store.Latest!.BindingKey);
        Assert.Equal(DesiredSyncStatus.ApplyFailedAcknowledged, store.Latest.Status);
        Assert.Equal(2, notified);

        store.Clear();
        Assert.Null(store.Latest);
        Assert.Equal(3, notified);

        store.Clear();   // already empty: no extra notification
        Assert.Equal(3, notified);
    }

    [Fact]
    public void ObservationStore_RejectsMalformedPublishes()
    {
        var store = new DesiredObservationStore();

        store.Publish("  ", Snapshot(), DesiredSyncStatus.AppliedAcknowledged, DateTimeOffset.UtcNow);
        store.Publish("key", null!, DesiredSyncStatus.AppliedAcknowledged, DateTimeOffset.UtcNow);

        Assert.Null(store.Latest);
    }

    // --- 8) path identity --------------------------------------------------

    [Fact]
    public void StoragePaths_MatchTheHistoricalProducerExpressions()
    {
        var binding = Binding();
        var baseDir = "/tmp/wakemesh-t118";

        Assert.Equal(System.IO.Path.Combine(baseDir, $"sync_manifest_{binding.ToStorageKey()}.json"),
            CloudSyncStoragePaths.Manifest(baseDir, binding));
        Assert.Equal(System.IO.Path.Combine(baseDir, $"applied_rev_{binding.ToStorageKey()}.json"),
            CloudSyncStoragePaths.AppliedRevision(baseDir, binding));
    }
}
