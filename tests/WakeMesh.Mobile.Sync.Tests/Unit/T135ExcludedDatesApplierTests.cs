using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.Sync.Tests.Fakes;
using Xunit;

namespace WakeMesh.Mobile.Sync.Tests.Unit;

/// <summary>
/// T135: server group <c>ExcludedDates</c> must reach the local store and the OS registration through the
/// real applier → <see cref="AlarmCoordinator"/> → <see cref="JsonFileAlarmStore"/> → real next-fire
/// calculation. The OS double is <see cref="FakeAlarmScheduler"/>; nothing here is an Android claim.
/// No fixed calendar dates: the excluded day is derived from the next-fire time computed at run time.
/// </summary>
public sealed class T135ExcludedDatesApplierTests : IDisposable
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wakemesh-t135-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly CloudSyncBinding _binding;
    private readonly string _alarmPath;
    private readonly JsonFileAlarmStore _store;
    private readonly FakeAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly FileCloudSyncManifestStore _manifestStore;
    private readonly MobileDesiredApplier _applier;

    public T135ExcludedDatesApplierTests()
    {
        Directory.CreateDirectory(_dir);
        _binding = new CloudSyncBinding("https://api.example.com", "owner", _deviceId, 1);
        _alarmPath = Path.Combine(_dir, "alarms.json");
        _store = new JsonFileAlarmStore(_alarmPath);
        _scheduler = new FakeAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(new FakeHolidayService()), _scheduler);
        _manifestStore = new FileCloudSyncManifestStore(_binding, Path.Combine(_dir, "manifest.json"));
        _applier = new MobileDesiredApplier(_binding, _coordinator, _manifestStore);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private static DateOnly LocalDate(DateTimeOffset instant)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Tokyo).DateTime);

    private static string Wire(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DesiredSnapshotDto Snapshot(long revision, params DesiredItemDto[] items)
        => new(_deviceId, revision, Guid.NewGuid(), items, Array.Empty<DesiredTombstoneDto>());

    private static DesiredItemDto Item(
        Guid scheduleId,
        string time = "07:00",
        string repeat = "weekly",
        int[]? days = null,
        string? date = null,
        IReadOnlyList<string>? excludedDates = null,
        string label = "G1")
        => new(
            ScheduleId: scheduleId,
            GroupId: "g1",
            Label: label,
            GroupEnabled: true,
            Repeat: repeat,
            Days: repeat == "weekly" ? days ?? new[] { 0, 1, 2, 3, 4, 5, 6 } : null,
            Date: date,
            TimeZone: "Asia/Tokyo",
            ExcludeHolidays: false,
            ExcludedDates: excludedDates ?? Array.Empty<string>(),
            Time: time,
            SoundKey: "chime",
            Volume: 80,
            Vibrate: true,
            Enabled: true,
            ModifiedRevision: 1);

    private async Task<DateOnly> SeedWeeklyAndReadNextFireAsync(Guid scheduleId)
    {
        var seed = await _applier.ApplyAsync(Snapshot(1, Item(scheduleId)));
        Assert.True(seed.Succeeded, Dump(seed));
        var seeded = await _store.GetByIdAsync(SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId));
        Assert.NotNull(seeded);
        Assert.Empty(seeded!.ExcludedDates);
        Assert.NotNull(seeded.NextFireTime);
        return LocalDate(seeded.NextFireTime!.Value);
    }

    private static string Dump(DesiredApplyResult result)
        => string.Join("; ", result.Failures.Select(f => $"{f.ScheduleId}:{f.Code}:{f.Message}"));

    // ---- (1) an excluded next-fire day moves the alarm one day later ---------

    [Fact]
    public async Task ExcludedDate_MovesNextFireOneDayLater_InStoreAndInOs_AndStampsTheFormat()
    {
        var scheduleId = Guid.NewGuid();
        var excludedDay = await SeedWeeklyAndReadNextFireAsync(scheduleId);

        var applied = await _applier.ApplyAsync(Snapshot(2, Item(scheduleId, excludedDates: new[] { Wire(excludedDay) })));
        Assert.True(applied.Succeeded, Dump(applied));

        var localId = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId);
        var stored = await _store.GetByIdAsync(localId);
        Assert.NotNull(stored);
        Assert.Equal(new[] { excludedDay }, stored!.ExcludedDates);
        Assert.Equal(excludedDay.AddDays(1), LocalDate(stored.NextFireTime!.Value));

        Assert.True(_scheduler.ScheduledAlarms.ContainsKey(localId));
        Assert.Equal(excludedDay.AddDays(1), LocalDate(_scheduler.ScheduledAlarms[localId].FireTime));

        var manifest = await _manifestStore.LoadAsync();
        Assert.Equal(CloudSyncManifest.CurrentAppliedItemFormat, manifest.AppliedItemFormat);
        Assert.Equal(1, manifest.AppliedItemFormat);
    }

    // ---- (2) removing the exclusion at a later revision restores the day -----

    [Fact]
    public async Task RemovingTheExclusion_AtTheNextRevision_RestoresTheOriginalDay()
    {
        var scheduleId = Guid.NewGuid();
        var originalDay = await SeedWeeklyAndReadNextFireAsync(scheduleId);

        Assert.True((await _applier.ApplyAsync(
            Snapshot(2, Item(scheduleId, excludedDates: new[] { Wire(originalDay) })))).Succeeded);

        var cleared = await _applier.ApplyAsync(Snapshot(3, Item(scheduleId, excludedDates: Array.Empty<string>())));
        Assert.True(cleared.Succeeded, Dump(cleared));

        var localId = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId);
        var stored = await _store.GetByIdAsync(localId);
        Assert.NotNull(stored);
        Assert.Empty(stored!.ExcludedDates);
        Assert.Equal(originalDay, LocalDate(stored.NextFireTime!.Value));
        Assert.Equal(originalDay, LocalDate(_scheduler.ScheduledAlarms[localId].FireTime));
    }

    // ---- (3) a once alarm whose only target day is excluded rings never ------

    [Fact]
    public async Task OnceAlarm_OnItsExcludedDate_HasNoNextFire_Unregisters_AndCancelsTheOsAlarm()
    {
        var scheduleId = Guid.NewGuid();
        var targetDay = LocalDate(DateTimeOffset.UtcNow).AddDays(3);

        var applied = await _applier.ApplyAsync(Snapshot(
            1,
            Item(scheduleId, repeat: "once", date: Wire(targetDay), excludedDates: new[] { Wire(targetDay) })));
        Assert.True(applied.Succeeded, Dump(applied));

        var localId = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId);
        var stored = await _store.GetByIdAsync(localId);
        Assert.NotNull(stored);
        Assert.Equal(targetDay, stored!.OnceOnDate);
        Assert.Equal(new[] { targetDay }, stored.ExcludedDates);
        Assert.Null(stored.NextFireTime);
        Assert.Equal(AlarmRegistrationStatus.Unregistered, stored.OsRegistrationStatus);
        Assert.DoesNotContain(localId, _scheduler.ScheduledAlarms.Keys);
        Assert.Contains(localId, _scheduler.CancelLog);
    }

    // ---- (4) a malformed date fails the snapshot in Phase 0, side effects 0 --

    [Fact]
    public async Task InvalidExcludedDate_RejectsTheWholeSnapshot_LeavingStoreOsAndManifestUntouched()
    {
        var retiring = Guid.NewGuid();
        Assert.True((await _applier.ApplyAsync(Snapshot(1, Item(retiring)))).Succeeded);

        var manifestBefore = await File.ReadAllBytesAsync(_manifestStore.FilePath);
        var storeBefore = await File.ReadAllBytesAsync(_alarmPath);
        var scheduleCallsBefore = _scheduler.ScheduleCalls;
        var cancelCallsBefore = _scheduler.CancelCalls;
        var storedIdsBefore = (await _store.GetAllAsync()).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var fresh = Guid.NewGuid();
        var malformed = Guid.NewGuid();
        var mixed = Snapshot(
            2,
            Item(fresh, label: "valid sibling"),
            Item(malformed, label: "bad excluded date", excludedDates: new[] { "2026/12/25" }));

        var result = await _applier.ApplyAsync(mixed);

        Assert.False(result.Succeeded);
        Assert.True(result.Completed);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(malformed, failure.ScheduleId);
        Assert.Equal("InvalidItem", failure.Code);

        Assert.Equal(storeBefore, await File.ReadAllBytesAsync(_alarmPath));
        Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(_manifestStore.FilePath));
        Assert.Equal(scheduleCallsBefore, _scheduler.ScheduleCalls);
        Assert.Equal(cancelCallsBefore, _scheduler.CancelCalls);

        var storedIdsAfter = (await _store.GetAllAsync()).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(storedIdsBefore, storedIdsAfter);
        Assert.DoesNotContain(storedIdsAfter, id => id.Contains(fresh.ToString("N"), StringComparison.Ordinal));
        // The omitted schedule was a delete candidate; the rejection must not have retired it.
        Assert.Contains(storedIdsAfter, id => id.Contains(retiring.ToString("N"), StringComparison.Ordinal));
        // The marker was not stamped by the rejected cycle.
        Assert.Equal(CloudSyncManifest.CurrentAppliedItemFormat, (await _manifestStore.LoadAsync()).AppliedItemFormat);
    }

    [Fact]
    public async Task NullExcludedDates_IsTreatedAsEmptyAndApplies()
    {
        var scheduleId = Guid.NewGuid();
        var item = Item(scheduleId) with { ExcludedDates = null! };

        var result = await _applier.ApplyAsync(Snapshot(1, item));

        Assert.True(result.Succeeded, Dump(result));
        var stored = await _store.GetByIdAsync(SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId));
        Assert.NotNull(stored);
        Assert.Empty(stored!.ExcludedDates);
        Assert.Equal(1, (await _manifestStore.LoadAsync()).AppliedItemFormat);
    }

    // ---- (5) JSON round-trip survives a restart, and legacy files load empty --

    [Fact]
    public async Task JsonRoundTrip_AndRescheduleAfterRestart_KeepTheExclusion()
    {
        var scheduleId = Guid.NewGuid();
        var excludedDay = await SeedWeeklyAndReadNextFireAsync(scheduleId);
        Assert.True((await _applier.ApplyAsync(
            Snapshot(2, Item(scheduleId, excludedDates: new[] { Wire(excludedDay) })))).Succeeded);

        // A restart means brand-new store/coordinator instances over the same files.
        var restartedStore = new JsonFileAlarmStore(_alarmPath);
        var loaded = Assert.Single(await restartedStore.GetAllAsync());
        Assert.Equal(new[] { excludedDay }, loaded.ExcludedDates);

        var bootScheduler = new FakeAlarmScheduler();
        var restartedCoordinator = new AlarmCoordinator(
            restartedStore,
            new AlarmScheduleCalculator(new FakeHolidayService()),
            bootScheduler);
        await restartedCoordinator.RescheduleAllActiveAsync();

        Assert.True(bootScheduler.ScheduledAlarms.ContainsKey(loaded.Id));
        Assert.Equal(excludedDay.AddDays(1), LocalDate(bootScheduler.ScheduledAlarms[loaded.Id].FireTime));

        // ToAlarmDefinition/FromAlarmDefinition keep the field too.
        var roundTripped = LocalAlarmModel.FromAlarmDefinition(loaded.ToAlarmDefinition(), loaded.DeviceId);
        Assert.Equal(new[] { excludedDay }, roundTripped.ExcludedDates);
    }

    [Fact]
    public async Task LegacyJsonWithoutTheKey_AndALiteralNull_LoadAsEmptyWithoutThrowing()
    {
        const string baseFields =
            "\"Id\":\"legacy-1\",\"Name\":\"legacy\",\"IsEnabled\":true,\"Time\":\"07:00\",\"RepeatKind\":1," +
            "\"ActiveWeekdays\":127,\"ExcludeHolidays\":false,\"DeviceId\":\"legacy-device\",\"TimeZoneId\":\"Asia/Tokyo\"";

        await File.WriteAllTextAsync(_alarmPath, "[{" + baseFields + "}]");
        var legacy = Assert.Single(await new JsonFileAlarmStore(_alarmPath).GetAllAsync());
        Assert.Empty(legacy.ExcludedDates);
        Assert.Empty(legacy.ToAlarmDefinition().ExcludedDates);
        Assert.NotNull(new AlarmScheduleCalculator(new FakeHolidayService())
            .CalculateNextFire(legacy, DateTimeOffset.UtcNow));

        await File.WriteAllTextAsync(_alarmPath, "[{" + baseFields + ",\"ExcludedDates\":null}]");
        var nulled = Assert.Single(await new JsonFileAlarmStore(_alarmPath).GetAllAsync());
        Assert.Empty(nulled.ExcludedDates);
        Assert.Empty(nulled.ToAlarmDefinition().ExcludedDates);
        Assert.NotNull(new AlarmScheduleCalculator(new FakeHolidayService())
            .CalculateNextFire(nulled, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ManifestClone_CarriesTheAppliedItemFormat()
    {
        var manifest = new CloudSyncManifest
        {
            ApiBaseUrl = "https://api.example.com",
            AccountId = "owner",
            ServerDeviceId = _deviceId,
            RegistrationGeneration = 1,
            AppliedItemFormat = CloudSyncManifest.CurrentAppliedItemFormat,
        };

        Assert.Equal(1, manifest.Clone().AppliedItemFormat);
        // A manifest written before T135 has no such key → 0 → the probe asks for one re-apply.
        Assert.Equal(0, new CloudSyncManifest().AppliedItemFormat);
        Assert.Equal(0, new CloudSyncManifest().Clone().AppliedItemFormat);
    }

    [Fact]
    public async Task Probe_IsTrueOnlyWhileTheMarkerIsBehind()
    {
        Assert.True(await _applier.RequiresReapplyAtEqualRevisionAsync());

        Assert.True((await _applier.ApplyAsync(Snapshot(1))).Succeeded);
        Assert.False(await _applier.RequiresReapplyAtEqualRevisionAsync());
    }
}
