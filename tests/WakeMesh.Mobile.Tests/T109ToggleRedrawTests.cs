using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T109 (T107 M1): after a user switches a row OFF, that row must be redrawn — the disabled alarm
/// must not keep a next-fire time, and the footer's enabled count must be recomputed.
/// LocalAlarmModel is a POCO (no INotifyPropertyChanged), so the view model replaces the item in the
/// observable collection instead of mutating it. These tests run the real JsonFileAlarmStore and the
/// recording MockAlarmScheduler through AlarmListViewModel.
/// </summary>
public sealed class T109ToggleRedrawTests : IDisposable
{
    private const string AlarmId = "t109-alarm-1";
    private const string ExpectedOff = "1 件のアラーム (0 件有効)";
    private const string ExpectedOn = "1 件のアラーム (1 件有効)";

    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly MockAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly AlarmListViewModel _vm;

    public T109ToggleRedrawTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t109_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "alarms.json");
        _store = new JsonFileAlarmStore(_filePath);
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _scheduler = new MockAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(_holidayService), _scheduler);
        _vm = new AlarmListViewModel(_coordinator, _holidayService, new LocalDeviceInfoProvider("t109-device"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static LocalAlarmModel NewAlarm(bool isEnabled) => new()
    {
        Id = AlarmId,
        Name = "T109 朝",
        IsEnabled = isEnabled,
        Time = new TimeOnly(7, 0),
        RepeatKind = AlarmRepeatKind.Weekly,
        ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
        DeviceId = "t109-device",
        TimeZoneId = "Asia/Tokyo"
    };

    /// <summary>
    /// Stored true → user OFF: the bound row instance is replaced (so the row redraws), the new row
    /// carries IsEnabled=false and no NextFireTime, and the footer reports 0 件有効. Switching back ON
    /// restores the next-fire time and 1 件有効.
    /// </summary>
    [Fact]
    public async Task UserOff_ReplacesRow_ClearsNextFire_AndRecomputesEnabledCount()
    {
        await _store.SaveAsync(NewAlarm(true));
        await _vm.LoadAlarmsAsync();

        Assert.Single(_vm.Alarms);
        Assert.Equal(ExpectedOn, _vm.StatusMessage);
        var row = _vm.Alarms[0];
        Assert.True(row.IsEnabled);
        Assert.NotNull(row.NextFireTime);

        var off = await _vm.SetAlarmEnabledFromUserAsync(row, false);

        Assert.True(off);
        // (1)+(4) the row the Switch binds to is a new instance with IsEnabled=false
        var offRow = _vm.Alarms[0];
        Assert.NotSame(row, offRow);
        Assert.False(offRow.IsEnabled);
        Assert.Equal(AlarmId, offRow.Id);
        Assert.Equal("T109 朝", offRow.Name);
        Assert.Equal(new TimeOnly(7, 0), offRow.Time);
        // (2) no next-fire time left on the disabled row
        Assert.Null(offRow.NextFireTime);
        // (3) footer says nothing is enabled
        Assert.Equal(ExpectedOff, _vm.StatusMessage);
        // saved state agrees with the redrawn row
        var storedOff = await _store.GetByIdAsync(AlarmId);
        Assert.NotNull(storedOff);
        Assert.False(storedOff!.IsEnabled);
        Assert.Null(storedOff.NextFireTime);
        Assert.Contains(AlarmId, _scheduler.CancelledAlarmIds);

        var on = await _vm.SetAlarmEnabledFromUserAsync(offRow, true);

        Assert.True(on);
        var onRow = _vm.Alarms[0];
        Assert.NotSame(offRow, onRow);
        Assert.True(onRow.IsEnabled);
        Assert.NotNull(onRow.NextFireTime);
        Assert.Equal(ExpectedOn, _vm.StatusMessage);
    }
}
