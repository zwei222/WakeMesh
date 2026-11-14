using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.Sync.Tests.Fakes;

namespace WakeMesh.Mobile.Sync.Tests.Unit;

/// <summary>
/// T22: API/Client wire days are fixed 0=Sun..6=Sat. Applier must map/validate that contract
/// into Core <see cref="Weekdays"/> bitflags (Monday bit0 .. Sunday bit6) without dual-encoding.
/// </summary>
public sealed class T22WeekdayContractApplierTests : IDisposable
{
    private readonly string _testDir;
    private readonly Guid _devId = Guid.NewGuid();
    private readonly CloudSyncBinding _binding;
    private readonly FakeAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly FileCloudSyncManifestStore _manifestStore;
    private readonly MobileDesiredApplier _applier;
    private readonly AlarmScheduleCalculator _calculator;
    private readonly FakeHolidayService _holidays;

    public T22WeekdayContractApplierTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-t22-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _binding = new CloudSyncBinding("https://api.example.com", "user-1", _devId, 1);
        _scheduler = new FakeAlarmScheduler();
        _holidays = new FakeHolidayService();
        _calculator = new AlarmScheduleCalculator(_holidays);
        _coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(_testDir, "alarms.json")),
            _calculator,
            _scheduler);
        _manifestStore = new FileCloudSyncManifestStore(_binding, Path.Combine(_testDir, "manifest.json"));
        _applier = new MobileDesiredApplier(_binding, _coordinator, _manifestStore);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    public static TheoryData<int, Weekdays, DayOfWeek> WireDayCases => new()
    {
        { 0, Weekdays.Sunday, DayOfWeek.Sunday },
        { 1, Weekdays.Monday, DayOfWeek.Monday },
        { 2, Weekdays.Tuesday, DayOfWeek.Tuesday },
        { 3, Weekdays.Wednesday, DayOfWeek.Wednesday },
        { 4, Weekdays.Thursday, DayOfWeek.Thursday },
        { 5, Weekdays.Friday, DayOfWeek.Friday },
        { 6, Weekdays.Saturday, DayOfWeek.Saturday },
    };

    [Theory]
    [MemberData(nameof(WireDayCases))]
    public async Task WireDay_MapsToCoreBit_AndNextFireLandsOnThatWeekday(
        int wireDay,
        Weekdays expectedBit,
        DayOfWeek expectedDow)
    {
        var scheduleId = Guid.NewGuid();
        var result = await _applier.ApplyAsync(Snapshot(Item(scheduleId, days: new[] { wireDay }, time: "09:00")));
        Assert.True(result.Succeeded, string.Join(";", result.Failures.Select(f => f.Code + ":" + f.Message)));

        var alarm = Assert.Single(await _coordinator.GetAllAsync());
        Assert.Equal(expectedBit, alarm.ActiveWeekdays);
        Assert.Equal(AlarmRepeatKind.Weekly, alarm.RepeatKind);

        // After Saturday 10:00 JST so every single-day weekly target is in the forward week (incl. Sunday).
        var after = new DateTimeOffset(2026, 3, 14, 10, 0, 0, TimeSpan.FromHours(9)); // Saturday
        var next = _calculator.CalculateNextFire(alarm, after);
        Assert.NotNull(next);
        Assert.Equal(expectedDow, next!.LocalDate.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 0), next.LocalTime);
        Assert.True(next.Instant > after);

        Assert.True(_scheduler.ScheduledAlarms.TryGetValue(alarm.Id, out var scheduled));
        Assert.Equal(expectedBit, scheduled!.Alarm.ActiveWeekdays);
    }

    [Fact]
    public async Task SundayWire0_ComputesNextFire_WhenAfterIsSaturday()
    {
        var scheduleId = Guid.NewGuid();
        Assert.True((await _applier.ApplyAsync(Snapshot(Item(scheduleId, days: new[] { 0 }, time: "07:15")))).Succeeded);

        var alarm = Assert.Single(await _coordinator.GetAllAsync());
        Assert.Equal(Weekdays.Sunday, alarm.ActiveWeekdays);

        var afterSat = new DateTimeOffset(2026, 6, 13, 8, 0, 0, TimeSpan.FromHours(9)); // Saturday
        var next = _calculator.CalculateNextFire(alarm, afterSat);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 6, 14), next!.LocalDate); // next calendar Sunday
        Assert.Equal(DayOfWeek.Sunday, next.LocalDate.DayOfWeek);
    }

    [Fact]
    public async Task SundayHolidayExclude_SkipsHolidaySunday_ToFollowingSunday()
    {
        var holidaySunday = new DateOnly(2026, 6, 14);
        _holidays.Holidays = new HashSet<DateOnly> { holidaySunday };

        var scheduleId = Guid.NewGuid();
        Assert.True((await _applier.ApplyAsync(Snapshot(Item(
            scheduleId,
            days: new[] { 0 },
            time: "07:00",
            excludeHolidays: true)))).Succeeded);

        var alarm = Assert.Single(await _coordinator.GetAllAsync());
        Assert.True(alarm.ExcludeHolidays);
        Assert.Equal(Weekdays.Sunday, alarm.ActiveWeekdays);

        var afterSat = new DateTimeOffset(2026, 6, 13, 8, 0, 0, TimeSpan.FromHours(9));
        var next = _calculator.CalculateNextFire(alarm, afterSat);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 6, 21), next!.LocalDate);
        Assert.Equal(DayOfWeek.Sunday, next.LocalDate.DayOfWeek);
    }

    [Fact]
    public async Task WeekCrossover_FridayOnly_FromThursday_LandsFriday_NotShiftedByOldSundayEncoding()
    {
        var scheduleId = Guid.NewGuid();
        Assert.True((await _applier.ApplyAsync(Snapshot(Item(scheduleId, days: new[] { 5 }, time: "06:30")))).Succeeded);

        var alarm = Assert.Single(await _coordinator.GetAllAsync());
        Assert.Equal(Weekdays.Friday, alarm.ActiveWeekdays);

        var afterThu = new DateTimeOffset(2026, 3, 12, 22, 0, 0, TimeSpan.FromHours(9)); // Thursday night
        var next = _calculator.CalculateNextFire(alarm, afterThu);
        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Friday, next!.LocalDate.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 3, 13), next.LocalDate);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task InvalidWireDay_Phase0InvalidItem_NoSideEffects(int badDay)
    {
        var seedId = Guid.NewGuid();
        Assert.True((await _applier.ApplyAsync(Snapshot(Item(seedId, days: new[] { 1, 2 })))).Succeeded);

        var revisionPath = Path.Combine(_testDir, "revision.json");
        var revisionStore = new DurableAppliedRevisionStore(_binding, revisionPath);
        await revisionStore.CommitAppliedRevisionAsync(_devId, 1);

        async Task<Dictionary<string, byte[]>> SnapshotBytes()
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(_testDir))
            {
                map[Path.GetFileName(path)] = await File.ReadAllBytesAsync(path);
            }

            return map;
        }

        var beforeBytes = await SnapshotBytes();
        var scheduleBefore = _scheduler.ScheduleCalls;
        var cancelBefore = _scheduler.CancelCalls;
        var storeBefore = (await _coordinator.GetAllAsync()).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var revisionBefore = await revisionStore.GetAppliedRevisionAsync(_devId);

        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var mixed = new DesiredSnapshotDto(
            _devId,
            2,
            Guid.NewGuid(),
            new[]
            {
                Item(good, days: new[] { 0, 6 }, time: "07:00", rev: 2),
                Item(bad, days: new[] { 1, badDay }, time: "08:00", rev: 2),
            },
            Array.Empty<DesiredTombstoneDto>());

        var result = await _applier.ApplyAsync(mixed);

        Assert.False(result.Succeeded);
        Assert.True(result.Completed);
        Assert.Contains(result.Failures, f => f.ScheduleId == bad && f.Code == "InvalidItem");
        Assert.DoesNotContain(result.Failures, f => f.ScheduleId == good);
        Assert.Equal(0, _scheduler.ScheduleCalls - scheduleBefore);
        Assert.Equal(0, _scheduler.CancelCalls - cancelBefore);

        var afterBytes = await SnapshotBytes();
        Assert.Equal(beforeBytes.Keys.OrderBy(k => k, StringComparer.Ordinal), afterBytes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in beforeBytes.Keys)
        {
            Assert.Equal(beforeBytes[key], afterBytes[key]);
        }

        var storeAfter = (await _coordinator.GetAllAsync()).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(storeBefore, storeAfter);
        Assert.Equal(revisionBefore, await revisionStore.GetAppliedRevisionAsync(_devId));
        Assert.DoesNotContain(storeAfter, id => id.Contains(good.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(storeAfter, id => id.Contains(seedId.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualLocalAlarm_AndCoreWeekdayBits_Unchanged_WhenDesiredAppliesSunday()
    {
        var manual = new LocalAlarmModel
        {
            Id = "manual-local-1",
            Name = "手動",
            Time = new TimeOnly(8, 0),
            IsEnabled = true,
            DeviceId = "local",
            ActiveWeekdays = Weekdays.Monday | Weekdays.Wednesday,
            RepeatKind = AlarmRepeatKind.Weekly,
            TimeZoneId = "Asia/Tokyo",
        };
        await _coordinator.SaveAndScheduleAsync(manual);

        var scheduleId = Guid.NewGuid();
        Assert.True((await _applier.ApplyAsync(Snapshot(Item(scheduleId, days: new[] { 0 }, time: "09:00")))).Succeeded);

        var all = await _coordinator.GetAllAsync();
        Assert.Equal(2, all.Count);
        var stillManual = Assert.Single(all, a => a.Id == "manual-local-1");
        Assert.Equal(Weekdays.Monday | Weekdays.Wednesday, stillManual.ActiveWeekdays);
        Assert.Equal("手動", stillManual.Name);

        // Core enum bit layout stays Monday=1<<0 .. Sunday=1<<6
        Assert.Equal(1 << 0, (int)Weekdays.Monday);
        Assert.Equal(1 << 6, (int)Weekdays.Sunday);
        Assert.Equal(Weekdays.Sunday, Assert.Single(all, a => a.Id != "manual-local-1").ActiveWeekdays);
    }

    [Fact]
    public async Task AllSevenWireDays_Together_MapFullWeek_WithoutAcceptingSeven()
    {
        var scheduleId = Guid.NewGuid();
        var days = new[] { 0, 1, 2, 3, 4, 5, 6 };
        Assert.True((await _applier.ApplyAsync(Snapshot(Item(scheduleId, days: days, time: "05:00")))).Succeeded);

        var alarm = Assert.Single(await _coordinator.GetAllAsync());
        var full =
            Weekdays.Sunday | Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday |
            Weekdays.Thursday | Weekdays.Friday | Weekdays.Saturday;
        Assert.Equal(full, alarm.ActiveWeekdays);
        Assert.Equal(127, (int)alarm.ActiveWeekdays);
    }

    private DesiredSnapshotDto Snapshot(params DesiredItemDto[] items) =>
        new(_devId, 1, Guid.NewGuid(), items, Array.Empty<DesiredTombstoneDto>());

    private static DesiredItemDto Item(
        Guid scheduleId,
        int[] days,
        string time = "07:00",
        bool excludeHolidays = false,
        long rev = 1) =>
        new(
            scheduleId,
            "g1",
            "T22",
            true,
            "weekly",
            days,
            null,
            "Asia/Tokyo",
            excludeHolidays,
            Array.Empty<string>(),
            time,
            "c",
            50,
            true,
            true,
            rev);
}
