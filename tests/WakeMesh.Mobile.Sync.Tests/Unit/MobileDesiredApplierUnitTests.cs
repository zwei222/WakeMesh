using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.Sync.Tests.Fakes;

namespace WakeMesh.Mobile.Sync.Tests.Unit;

public sealed class MobileDesiredApplierUnitTests : IDisposable
{
    private readonly string _testDir;
    private readonly Guid _devId = Guid.NewGuid();
    private readonly CloudSyncBinding _binding;
    private readonly JsonFileAlarmStore _alarmStore;
    private readonly FakeAlarmScheduler _scheduler;
    private readonly AlarmScheduleCalculator _calculator;
    private readonly AlarmCoordinator _coordinator;
    private readonly FileCloudSyncManifestStore _manifestStore;
    private readonly MobileDesiredApplier _applier;

    public MobileDesiredApplierUnitTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-applier-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _binding = new CloudSyncBinding("https://api.example.com", "user-1", _devId, 1);
        var alarmFile = Path.Combine(_testDir, "alarms.json");
        _alarmStore = new JsonFileAlarmStore(alarmFile);
        _scheduler = new FakeAlarmScheduler();
        _calculator = new AlarmScheduleCalculator(new FakeHolidayService());
        _coordinator = new AlarmCoordinator(_alarmStore, _calculator, _scheduler);

        var manifestFile = Path.Combine(_testDir, "manifest.json");
        _manifestStore = new FileCloudSyncManifestStore(_binding, manifestFile);
        _applier = new MobileDesiredApplier(_binding, _coordinator, _manifestStore);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Add_AppliesNewItem_PersistsToStore_SchedulesInOS_AndUpdatesManifest()
    {
        var scheduleId = Guid.NewGuid();
        var snapshot = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(
                    ScheduleId: scheduleId,
                    GroupId: "group-1",
                    Label: "Morning Alarm",
                    GroupEnabled: true,
                    Repeat: "weekly",
                    Days: new[] { 1, 2, 3, 4, 5 },
                    Date: null,
                    TimeZone: "Asia/Tokyo",
                    ExcludeHolidays: true,
                    ExcludedDates: Array.Empty<string>(),
                    Time: "07:30",
                    SoundKey: "chime",
                    Volume: 90,
                    Vibrate: true,
                    Enabled: true,
                    ModifiedRevision: 1)
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());

        var result = await _applier.ApplyAsync(snapshot);

        Assert.True(result.Succeeded);
        Assert.True(result.Completed);
        Assert.Empty(result.Failures);

        // Store verification
        var alarms = await _coordinator.GetAllAsync();
        Assert.Single(alarms);
        var alarm = alarms[0];
        Assert.Equal("Morning Alarm", alarm.Name);
        Assert.Equal(new TimeOnly(7, 30), alarm.Time);
        Assert.Equal(AlarmRepeatKind.Weekly, alarm.RepeatKind);
        // Wire 1..5 = Mon..Fri under 0=Sun..6=Sat contract.
        Assert.Equal(
            Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
            alarm.ActiveWeekdays);
        Assert.True((alarm.ActiveWeekdays & Weekdays.Monday) != 0);
        Assert.True((alarm.ActiveWeekdays & Weekdays.Friday) != 0);
        Assert.True((alarm.ActiveWeekdays & Weekdays.Sunday) == 0);
        Assert.Equal(AlarmRegistrationStatus.Scheduled, alarm.OsRegistrationStatus);
        Assert.Equal(AlarmSyncStatus.Synced, alarm.SyncStatus);

        // Fake OS scheduler verification (Note: Fake OS double; not real Android OS execution)
        Assert.True(_scheduler.ScheduledAlarms.ContainsKey(alarm.Id));

        // Manifest verification
        var manifest = await _manifestStore.LoadAsync();
        Assert.True(manifest.Schedules.ContainsKey(scheduleId));
        Assert.Equal(alarm.Id, manifest.Schedules[scheduleId].LocalAlarmId);
    }

    [Fact]
    public async Task Update_ModifiesExistingAlarm_AndUpdatesOS()
    {
        var scheduleId = Guid.NewGuid();
        // 1. Initial Apply
        var initial = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(
                    ScheduleId: scheduleId,
                    GroupId: "group-1",
                    Label: "V1",
                    GroupEnabled: true,
                    Repeat: "weekly",
                    Days: new[] { 1 },
                    Date: null,
                    TimeZone: "Asia/Tokyo",
                    ExcludeHolidays: false,
                    ExcludedDates: Array.Empty<string>(),
                    Time: "06:00",
                    SoundKey: "sound1",
                    Volume: 50,
                    Vibrate: false,
                    Enabled: true,
                    ModifiedRevision: 1)
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());
        await _applier.ApplyAsync(initial);

        var firstAlarms = await _coordinator.GetAllAsync();
        Assert.Single(firstAlarms);
        var initialId = firstAlarms[0].Id;

        // 2. Update to V2
        var updated = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 2,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(
                    ScheduleId: scheduleId,
                    GroupId: "group-1",
                    Label: "V2 Updated",
                    GroupEnabled: true,
                    Repeat: "weekly",
                    Days: new[] { 1, 2 },
                    Date: null,
                    TimeZone: "Asia/Tokyo",
                    ExcludeHolidays: true,
                    ExcludedDates: Array.Empty<string>(),
                    Time: "08:15",
                    SoundKey: "sound2",
                    Volume: 80,
                    Vibrate: true,
                    Enabled: true,
                    ModifiedRevision: 2)
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());
        var res = await _applier.ApplyAsync(updated);

        Assert.True(res.Succeeded);
        var currentAlarms = await _coordinator.GetAllAsync();
        Assert.Single(currentAlarms);
        var current = currentAlarms[0];

        // Id preserved across update
        Assert.Equal(initialId, current.Id);
        Assert.Equal("V2 Updated", current.Name);
        Assert.Equal(new TimeOnly(8, 15), current.Time);
        Assert.Equal(80, current.VolumePercent);
        Assert.True(current.EnableVibration);
    }

    [Fact]
    public async Task RemoveAndTombstone_CancelsOS_DeletesFromStore_AndRemovesFromManifest()
    {
        var sched1 = Guid.NewGuid();
        var sched2 = Guid.NewGuid();

        // 1. Apply two schedules
        var snap1 = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(sched1, "g1", "S1", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "06:00", "c", 50, true, true, 1),
                new DesiredItemDto(sched2, "g1", "S2", true, "weekly", new[] { 2 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "07:00", "c", 50, true, true, 1),
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());
        await _applier.ApplyAsync(snap1);

        var alarmsAfterSnap1 = await _coordinator.GetAllAsync();
        Assert.Equal(2, alarmsAfterSnap1.Count);

        // 2. S1 removed (missing from items) and S2 tombstoned
        var snap2 = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 2,
            ReceiptId: Guid.NewGuid(),
            Items: Array.Empty<DesiredItemDto>(),
            Tombstones: new[] { new DesiredTombstoneDto(sched2) });

        var res2 = await _applier.ApplyAsync(snap2);
        Assert.True(res2.Succeeded);

        var remainingAlarms = await _coordinator.GetAllAsync();
        Assert.Empty(remainingAlarms);
        Assert.Empty(_scheduler.ScheduledAlarms);

        var manifest = await _manifestStore.LoadAsync();
        Assert.Empty(manifest.Schedules);
    }

    [Fact]
    public async Task ManualLocalAlarms_AreNeverTouchedOrDeleted_DuringCloudSync()
    {
        // 1. User created a manual alarm locally before sync
        var manualAlarm = new LocalAlarmModel
        {
            Id = "manual-alarm-999",
            Name = "My Cooking Timer",
            Time = new TimeOnly(12, 0),
            RepeatKind = AlarmRepeatKind.Once,
            // Keep the fixture in the future regardless of the test's time of day.
            OnceOnDate = DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
            IsEnabled = true,
            SyncStatus = AlarmSyncStatus.LocalOnly
        };
        await _coordinator.SaveAndScheduleAsync(manualAlarm);

        // Verify manual alarm scheduled
        Assert.True(_scheduler.ScheduledAlarms.ContainsKey(manualAlarm.Id));

        // 2. Cloud sync applies server desired schedules
        var sched = Guid.NewGuid();
        var snap = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(sched, "g1", "Work Alarm", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "07:00", "c", 50, true, true, 1)
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());
        await _applier.ApplyAsync(snap);

        var alarmsAfterSync = await _coordinator.GetAllAsync();
        Assert.Equal(2, alarmsAfterSync.Count);
        Assert.Contains(alarmsAfterSync, a => a.Id == "manual-alarm-999" && a.Name == "My Cooking Timer");

        // 3. Cloud sync retires/removes the server schedule
        var emptySnap = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 2,
            ReceiptId: Guid.NewGuid(),
            Items: Array.Empty<DesiredItemDto>(),
            Tombstones: new[] { new DesiredTombstoneDto(sched) });
        await _applier.ApplyAsync(emptySnap);

        // 4. Verify manual alarm is STILL intact in both store and scheduler
        var alarmsAfterRemoval = await _coordinator.GetAllAsync();
        Assert.Single(alarmsAfterRemoval);
        Assert.Equal("manual-alarm-999", alarmsAfterRemoval[0].Id);
        Assert.Equal("My Cooking Timer", alarmsAfterRemoval[0].Name);
        Assert.True(_scheduler.ScheduledAlarms.ContainsKey("manual-alarm-999"));
    }

    [Fact]
    public async Task PartialOSFailure_ReturnsSucceededFalse_WithTypedFailures()
    {
        var schedOk = Guid.NewGuid();
        var schedFail = Guid.NewGuid();

        var expectedFailAlarmId = $"sync-{_binding.ToStorageKey()}-{schedFail:N}";
        _scheduler.FailOnScheduleIds.Add(expectedFailAlarmId);

        var snap = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(schedOk, "g1", "Good", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "06:00", "c", 50, true, true, 1),
                new DesiredItemDto(schedFail, "g1", "Bad", true, "weekly", new[] { 2 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "07:00", "c", 50, true, true, 1),
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());

        var result = await _applier.ApplyAsync(snap);

        Assert.False(result.Succeeded);
        Assert.True(result.Completed);
        Assert.Single(result.Failures);
        Assert.Equal(schedFail, result.Failures[0].ScheduleId);
        Assert.Equal("OsRegistrationFailed", result.Failures[0].Code);
    }

    [Fact]
    public async Task CorruptedManifest_ThrowsCorruptedSyncManifestException_NeverDeletesLocalAlarms()
    {
        // 1. Existing local manual alarm
        var manualAlarm = new LocalAlarmModel
        {
            Id = "manual-1",
            Name = "Manual",
            Time = new TimeOnly(9, 0),
            IsEnabled = true
        };
        await _coordinator.SaveAndScheduleAsync(manualAlarm);

        // 2. Corrupt manifest file on disk
        await File.WriteAllTextAsync(_manifestStore.FilePath, "{ invalid json corrupt");

        var snap = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: Array.Empty<DesiredItemDto>(),
            Tombstones: Array.Empty<DesiredTombstoneDto>());

        // Apply must throw CorruptedSyncManifestException and NOT delete manual alarm
        await Assert.ThrowsAsync<CorruptedSyncManifestException>(() => _applier.ApplyAsync(snap));

        var remaining = await _coordinator.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal("manual-1", remaining[0].Id);
    }

    [Fact]
    public async Task CanceledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var snap = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: Array.Empty<DesiredItemDto>(),
            Tombstones: Array.Empty<DesiredTombstoneDto>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _applier.ApplyAsync(snap, cts.Token));
    }

    [Theory]
    [InlineData(true)]  // Days = null
    [InlineData(false)] // Days = empty array
    public async Task WeeklyEmptyOrNullDays_WithValidSiblingAndDeleteCandidate_RejectsBeforeAnySideEffect(bool nullDays)
    {
        // Seed: applied weekly schedule becomes a delete-candidate when omitted from the next snapshot.
        var retiring = Guid.NewGuid();
        var seed = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 1,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(
                    retiring, "g1", "Retiring", true, "weekly", new[] { 1, 2, 3 }, null,
                    "Asia/Tokyo", false, Array.Empty<string>(), "06:00", "c", 50, true, true, 1)
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());
        Assert.True((await _applier.ApplyAsync(seed)).Succeeded);

        // Durable revision sits beside store/manifest; applier must not touch it on Phase-0 reject.
        var revisionPath = Path.Combine(_testDir, "revision.json");
        var revisionStore = new DurableAppliedRevisionStore(_binding, revisionPath);
        await revisionStore.CommitAppliedRevisionAsync(_devId, 1);

        static async Task<Dictionary<string, byte[]>> SnapshotBytes(string dir)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(dir))
            {
                map[Path.GetFileName(path)] = await File.ReadAllBytesAsync(path);
            }

            return map;
        }

        var beforeBytes = await SnapshotBytes(_testDir);
        var scheduleBefore = _scheduler.ScheduleCalls;
        var cancelBefore = _scheduler.CancelCalls;
        var storeBefore = (await _coordinator.GetAllAsync()).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var manifestBefore = (await _manifestStore.LoadAsync()).Schedules
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.LocalAlarmId, kv.Value.GroupId, kv.Value.ModifiedRevision))
            .ToArray();
        var revisionBefore = await revisionStore.GetAppliedRevisionAsync(_devId);

        var good = Guid.NewGuid();
        var emptyDays = Guid.NewGuid();
        int[]? days = nullDays ? null : Array.Empty<int>();
        var mixed = new DesiredSnapshotDto(
            DeviceId: _devId,
            Revision: 2,
            ReceiptId: Guid.NewGuid(),
            Items: new[]
            {
                new DesiredItemDto(
                    good, "g1", "Good", true, "weekly", new[] { 1, 2, 3, 4, 5 }, null,
                    "Asia/Tokyo", false, Array.Empty<string>(), "07:00", "c", 50, true, true, 2),
                new DesiredItemDto(
                    emptyDays, "g1", "EmptyDays", true, "weekly", days, null,
                    "Asia/Tokyo", false, Array.Empty<string>(), "08:00", "c", 50, true, true, 2)
            },
            Tombstones: Array.Empty<DesiredTombstoneDto>());

        var result = await _applier.ApplyAsync(mixed);

        Assert.False(result.Succeeded);
        Assert.True(result.Completed);
        Assert.Contains(result.Failures, f => f.ScheduleId == emptyDays && f.Code == "InvalidItem");
        Assert.Equal(0, _scheduler.ScheduleCalls - scheduleBefore);
        Assert.Equal(0, _scheduler.CancelCalls - cancelBefore);

        var afterBytes = await SnapshotBytes(_testDir);
        Assert.Equal(beforeBytes.Keys.OrderBy(k => k, StringComparer.Ordinal), afterBytes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in beforeBytes.Keys)
        {
            Assert.Equal(beforeBytes[key], afterBytes[key]);
        }

        var storeAfter = (await _coordinator.GetAllAsync()).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(storeBefore, storeAfter);
        var manifestAfter = (await _manifestStore.LoadAsync()).Schedules
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.LocalAlarmId, kv.Value.GroupId, kv.Value.ModifiedRevision))
            .ToArray();
        Assert.Equal(manifestBefore, manifestAfter);
        Assert.Equal(revisionBefore, await revisionStore.GetAppliedRevisionAsync(_devId));
        Assert.DoesNotContain(storeAfter, id => id.Contains(good.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(storeAfter, id => id.Contains(retiring.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferentBindings_AreCompletelyIsolated()
    {
        var otherDevId = Guid.NewGuid();
        var otherBinding = new CloudSyncBinding("https://api.example.com", "other-user", otherDevId, 1);
        var otherManifestStore = new FileCloudSyncManifestStore(otherBinding, Path.Combine(_testDir, "manifest_other.json"));
        var otherApplier = new MobileDesiredApplier(otherBinding, _coordinator, otherManifestStore);

        // Apply to first binding
        var sched1 = Guid.NewGuid();
        await _applier.ApplyAsync(new DesiredSnapshotDto(
            _devId, 1, Guid.NewGuid(),
            new[] { new DesiredItemDto(sched1, "g1", "User1 Alarm", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "06:00", "c", 50, true, true, 1) },
            Array.Empty<DesiredTombstoneDto>()));

        // Apply to second binding
        var sched2 = Guid.NewGuid();
        await otherApplier.ApplyAsync(new DesiredSnapshotDto(
            otherDevId, 1, Guid.NewGuid(),
            new[] { new DesiredItemDto(sched2, "g2", "User2 Alarm", true, "weekly", new[] { 2 }, null, "Asia/Tokyo", false, Array.Empty<string>(), "07:00", "c", 50, true, true, 1) },
            Array.Empty<DesiredTombstoneDto>()));

        var alarms = await _coordinator.GetAllAsync();
        Assert.Equal(2, alarms.Count);

        // Tombstone in second binding only deletes second alarm
        await otherApplier.ApplyAsync(new DesiredSnapshotDto(
            otherDevId, 2, Guid.NewGuid(),
            Array.Empty<DesiredItemDto>(),
            new[] { new DesiredTombstoneDto(sched2) }));

        var remaining = await _coordinator.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal("User1 Alarm", remaining[0].Name);
    }
}
