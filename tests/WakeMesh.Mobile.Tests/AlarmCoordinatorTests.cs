using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class MockAlarmScheduler : IAlarmScheduler
{
    public List<(string Id, DateTimeOffset Time)> ScheduledAlarms { get; } = new();
    public List<string> CancelledAlarmIds { get; } = new();

    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
    {
        ScheduledAlarms.Add((alarm.Id, fireTime));
        return Task.CompletedTask;
    }

    public Task CancelAlarmAsync(string alarmId)
    {
        CancelledAlarmIds.Add(alarmId);
        return Task.CompletedTask;
    }
}

public class AlarmCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly AlarmScheduleCalculator _calculator;
    private readonly MockAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;

    public AlarmCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_coord_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "alarms.json");
        _store = new JsonFileAlarmStore(_filePath);
        _holidayService = new JapaneseHolidayService();
        _calculator = new AlarmScheduleCalculator(_holidayService);
        _scheduler = new MockAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, _calculator, _scheduler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task SaveAndScheduleAsync_WhenEnabled_SavesAndSchedulesFutureAlarm()
    {
        // 常に未来となる翌日木曜日
        var alarm = new LocalAlarmModel
        {
            Id = "coord-1",
            Name = "朝活",
            IsEnabled = true,
            Time = new TimeOnly(6, 0),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
            DeviceId = "dev-test",
            TimeZoneId = "Asia/Tokyo"
        };

        var scheduledTime = await _coordinator.SaveAndScheduleAsync(alarm);

        Assert.NotNull(scheduledTime);
        Assert.Single(_scheduler.ScheduledAlarms);
        Assert.Equal("coord-1", _scheduler.ScheduledAlarms[0].Id);

        var stored = await _store.GetByIdAsync("coord-1");
        Assert.NotNull(stored);
        Assert.Equal(scheduledTime, stored.NextFireTime);
    }

    [Fact]
    public async Task SaveAndScheduleAsync_WhenDisabled_CancelsAndClearsNextFireTime()
    {
        var alarm = new LocalAlarmModel
        {
            Id = "coord-2",
            Name = "休止中アラーム",
            IsEnabled = false,
            Time = new TimeOnly(7, 0),
            DeviceId = "dev-test"
        };

        var scheduledTime = await _coordinator.SaveAndScheduleAsync(alarm);

        Assert.Null(scheduledTime);
        Assert.Contains("coord-2", _scheduler.CancelledAlarmIds);

        var stored = await _store.GetByIdAsync("coord-2");
        Assert.NotNull(stored);
        Assert.Null(stored.NextFireTime);
    }

    [Fact]
    public async Task DeleteAndCancelAsync_DeletesFromStoreAndCancelsFromScheduler()
    {
        var alarm = new LocalAlarmModel
        {
            Id = "coord-del",
            Name = "削除アラーム",
            IsEnabled = true,
            DeviceId = "dev-test"
        };
        await _store.SaveAsync(alarm);

        await _coordinator.DeleteAndCancelAsync("coord-del");

        Assert.Contains("coord-del", _scheduler.CancelledAlarmIds);
        var stored = await _store.GetByIdAsync("coord-del");
        Assert.Null(stored);
    }
}
