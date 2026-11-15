using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T135: neither the manual editor's model rebuild nor the alarm list's row replacement may drop
/// <see cref="LocalAlarmModel.ExcludedDates"/>. The editor has no excluded-date UI, so an ordinary save
/// must carry the stored dates through unchanged, while a brand-new alarm starts empty.
/// </summary>
public sealed class T135ExcludedDatesPreservationTests : IDisposable
{
    private const string DeviceId = "t135-device";

    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly MockAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;

    public T135ExcludedDatesPreservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t135_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _scheduler = new MockAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(_holidayService), _scheduler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private static LocalAlarmModel NewAlarm(string id, params DateOnly[] excludedDates) => new()
    {
        Id = id,
        Name = "T135 朝",
        IsEnabled = true,
        Time = new TimeOnly(7, 0),
        RepeatKind = AlarmRepeatKind.Weekly,
        ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
        SnoozeMinutes = 7,
        DeviceId = DeviceId,
        TimeZoneId = "Asia/Tokyo",
        ExcludedDates = excludedDates.ToList(),
    };

    // ---- the editor: an existing row keeps its dates ------------------------

    [Fact]
    public async Task EditingAnExistingAlarm_KeepsItsExcludedDates()
    {
        var excluded = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        await _store.SaveAsync(NewAlarm("edit-1", excluded));

        var vm = new AlarmEditViewModel(_coordinator, new LocalDeviceInfoProvider(DeviceId));
        vm.LoadAlarm(await _store.GetByIdAsync("edit-1"));
        Assert.Equal("アラームの編集", vm.PageTitle);   // edit mode, not create

        vm.Name = "T135 朝 (変更)";
        vm.Time = new TimeOnly(18, 0);   // T133 regression surface: the chosen time must still reach the model
        Assert.True(await vm.SaveAlarmAsync());

        var saved = await _store.GetByIdAsync("edit-1");
        Assert.NotNull(saved);
        Assert.Equal("T135 朝 (変更)", saved!.Name);
        Assert.Equal(new TimeOnly(18, 0), saved.Time);
        Assert.Equal(7, saved.SnoozeMinutes);              // T133 snooze value not reset by the rebuild
        Assert.Equal(new[] { excluded }, saved.ExcludedDates);
        Assert.Equal(new[] { excluded }, saved.ToAlarmDefinition().ExcludedDates.OrderBy(d => d).ToList());
    }

    [Fact]
    public async Task CreatingANewAlarm_StartsWithNoExcludedDates()
    {
        var vm = new AlarmEditViewModel(_coordinator, new LocalDeviceInfoProvider(DeviceId));
        vm.Name = "新規";
        vm.Time = new TimeOnly(6, 30);

        Assert.True(await vm.SaveAlarmAsync());

        var saved = await _store.GetByIdAsync(vm.Id);
        Assert.NotNull(saved);
        Assert.Empty(saved!.ExcludedDates);
    }

    // ---- the list: row replacement keeps the dates --------------------------

    [Fact]
    public async Task TogglingARowOff_KeepsItsExcludedDatesInTheListAndTheStore()
    {
        var excluded = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
        await _store.SaveAsync(NewAlarm("toggle-1", excluded));

        var vm = new AlarmListViewModel(_coordinator, _holidayService, new LocalDeviceInfoProvider(DeviceId));
        await vm.LoadAlarmsAsync();
        var row = Assert.Single(vm.Alarms);
        Assert.Equal(new[] { excluded }, row.ExcludedDates);

        Assert.True(await vm.SetAlarmEnabledFromUserAsync(row, false));

        var replaced = Assert.Single(vm.Alarms);
        Assert.False(replaced.IsEnabled);
        Assert.Null(replaced.NextFireTime);
        Assert.Equal(new[] { excluded }, replaced.ExcludedDates);

        var stored = await _store.GetByIdAsync("toggle-1");
        Assert.NotNull(stored);
        Assert.False(stored!.IsEnabled);
        Assert.Equal(new[] { excluded }, stored.ExcludedDates);
        Assert.Equal(7, stored.SnoozeMinutes);
    }

    [Fact]
    public async Task TogglingBackOn_StillKeepsTheExcludedDates()
    {
        var excluded = DateOnly.FromDateTime(DateTime.Today).AddDays(3);
        await _store.SaveAsync(NewAlarm("toggle-2", excluded));

        var vm = new AlarmListViewModel(_coordinator, _holidayService, new LocalDeviceInfoProvider(DeviceId));
        await vm.LoadAlarmsAsync();
        Assert.True(await vm.SetAlarmEnabledFromUserAsync(vm.Alarms[0], false));
        Assert.True(await vm.SetAlarmEnabledFromUserAsync(vm.Alarms[0], true));

        var row = Assert.Single(vm.Alarms);
        Assert.True(row.IsEnabled);
        Assert.Equal(new[] { excluded }, row.ExcludedDates);
        Assert.NotNull(row.NextFireTime);
        Assert.Equal(new[] { excluded }, (await _store.GetByIdAsync("toggle-2"))!.ExcludedDates);
    }
}
