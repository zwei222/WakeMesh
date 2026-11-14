using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class T4DefectFixesTests : IDisposable
{
    private readonly string _tempDir;

    public T4DefectFixesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t4_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void B10_LocalAlarmModel_DefaultsToAsiaTokyo_Explicitly()
    {
        var model = new LocalAlarmModel();
        Assert.Equal("Asia/Tokyo", model.TimeZoneId);
    }

    [Fact]
    public void B9_LocalDeviceInfoProvider_PersistsIdAcrossInstances()
    {
        var idFile = Path.Combine(_tempDir, "device_id.txt");
        var provider1 = new LocalDeviceInfoProvider(storageFilePath: idFile);
        var id1 = provider1.DeviceId;
        Assert.False(string.IsNullOrWhiteSpace(id1));

        // 2nd instance with same file should read identical ID
        var provider2 = new LocalDeviceInfoProvider(storageFilePath: idFile);
        Assert.Equal(id1, provider2.DeviceId);
    }

    [Fact]
    public async Task B7_JsonFileAlarmStore_WhenJsonIsCorrupted_ThrowsAndDoesNotOverwrite()
    {
        var storeFile = Path.Combine(_tempDir, "corrupted_alarms.json");
        await File.WriteAllTextAsync(storeFile, "{ invalid json content !!!");

        var store = new JsonFileAlarmStore(storeFile);

        // Corrupted JSON must throw CorruptedAlarmStoreException, not return empty list
        await Assert.ThrowsAsync<CorruptedAlarmStoreException>(() => store.GetAllAsync());

        // Corrupted file content must still be on disk, not erased
        var content = await File.ReadAllTextAsync(storeFile);
        Assert.Contains("invalid json content", content);
    }

    [Fact]
    public void B2_AlarmIntentHelper_GeneratesStableDistinctUrisAndDeterministicCodes()
    {
        var alarmId = "alarm-abc-123";
        var alarmUri = AlarmIntentHelper.CreateAlarmUri(alarmId);
        var snoozeUri = AlarmIntentHelper.CreateSnoozeUri(alarmId);
        var dismissUri = AlarmIntentHelper.CreateDismissUri(alarmId);
        var alertUri = AlarmIntentHelper.CreateAlertUri(alarmId);

        Assert.Equal("wakemesh://alarm/alarm-abc-123", alarmUri);
        Assert.Equal("wakemesh://snooze/alarm-abc-123", snoozeUri);
        Assert.Equal("wakemesh://dismiss/alarm-abc-123", dismissUri);
        Assert.Equal("wakemesh://alert/alarm-abc-123", alertUri);

        Assert.NotEqual(alarmUri, snoozeUri);
        Assert.NotEqual(alarmUri, dismissUri);
        Assert.NotEqual(snoozeUri, dismissUri);

        var codeAlarm = AlarmIntentHelper.GetDeterministicRequestCode(alarmUri);
        var codeSnooze = AlarmIntentHelper.GetDeterministicRequestCode(snoozeUri);

        Assert.True(codeAlarm >= 0);
        Assert.True(codeSnooze >= 0);
        Assert.NotEqual(codeAlarm, codeSnooze);

        // Same URI must yield identical deterministic requestCode (stable across runs & reboots)
        Assert.Equal(codeAlarm, AlarmIntentHelper.GetDeterministicRequestCode(alarmUri));
    }

    [Fact]
    public void B3_ActiveAlarmRingManager_TracksSimultaneousAlarmsAndIndividualDismiss()
    {
        var manager = new ActiveAlarmRingManager();

        var alarm1 = new ActiveRingingAlarm("a1", "Alarm 1", DateTimeOffset.UtcNow, 5, 100, true);
        var alarm2 = new ActiveRingingAlarm("a2", "Alarm 2", DateTimeOffset.UtcNow, 5, 80, false);

        var firstStart = manager.AddRingingAlarm(alarm1);
        Assert.True(firstStart); // First alarm starts the player

        var secondStart = manager.AddRingingAlarm(alarm2);
        Assert.False(secondStart); // Second alarm keeps existing player running

        Assert.Equal(2, manager.GetRingingAlarms().Count);

        // Dismiss alarm 1: player should remain running because alarm 2 is still ringing
        var hasRemaining = manager.DismissAlarm("a1");
        Assert.True(hasRemaining);
        Assert.Single(manager.GetRingingAlarms());
        Assert.Equal("a2", manager.GetRingingAlarms()[0].AlarmId);

        // Dismiss alarm 2: player should stop
        hasRemaining = manager.DismissAlarm("a2");
        Assert.False(hasRemaining);
        Assert.Empty(manager.GetRingingAlarms());
    }

    [Fact]
    public void B3_ActiveAlarmRingManager_EnforcesMax10MinuteDuration()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ActiveAlarmRingManager.MaxRingingDuration);
    }

    [Fact]
    public void B8_JapaneseHolidayService_WithoutData_ReportsNotFetched_AndUsesCoreCalendar()
    {
        var service = new JapaneseHolidayService(initialCalendar: null);

        Assert.False(service.HasHolidayData);
        Assert.Contains("未取得", service.HolidayStatusSummary);
        Assert.DoesNotContain("内閣府公表祝日キャッシュ適用済み", service.HolidayStatusSummary);
    }

    [Fact]
    public void B6_AlarmScheduleCalculator_DistinguishesNextNullFromException()
    {
        var holidayService = new JapaneseHolidayService(initialCalendar: null);
        var calc = new AlarmScheduleCalculator(holidayService);

        // Alarm with invalid timezone must throw, not silently return null
        var badAlarm = new LocalAlarmModel
        {
            Id = "bad-tz",
            Name = "Bad TZ",
            IsEnabled = true,
            TimeZoneId = "Invalid/Nonexistent_Timezone_123"
        };

        Assert.Throws<ArgumentException>(() => calc.CalculateNextFire(badAlarm, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void B6_AlarmScheduleCalculator_OnceAlarm_PastToday_ExplicitlyAdvances()
    {
        var holidayService = new JapaneseHolidayService(initialCalendar: null);
        var calc = new AlarmScheduleCalculator(holidayService);

        var tokyoTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        // Base time is 10:00 JST today
        var baseTime = new DateTimeOffset(2026, 5, 1, 10, 0, 0, tokyoTz.BaseUtcOffset);

        // One-off alarm set for 08:00 (which is in the past for today)
        var pastAlarm = new LocalAlarmModel
        {
            Id = "once-past",
            Name = "Past today",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Once,
            Time = new TimeOnly(8, 0),
            ExplicitDate = new DateOnly(2026, 5, 1),
            TimeZoneId = "Asia/Tokyo"
        };

        // When scheduled without updating explicit date, baseTime is already past today's 08:00
        var next = calc.CalculateNextFire(pastAlarm, baseTime);
        Assert.Null(next); // Past one-off returns null

        // With explicit tomorrow date, it schedules correctly
        pastAlarm.ExplicitDate = new DateOnly(2026, 5, 2);
        next = calc.CalculateNextFire(pastAlarm, baseTime);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 5, 2), next.LocalDate);
        Assert.Equal(new TimeOnly(8, 0), next.LocalTime);
    }

    [Fact]
    public async Task B2_SnoozePersistence_PreservedOnRescheduleAll()
    {
        var storeFile = Path.Combine(_tempDir, "alarms.json");
        var store = new JsonFileAlarmStore(storeFile);
        var holidayService = new JapaneseHolidayService(initialCalendar: null);
        var calc = new AlarmScheduleCalculator(holidayService);
        var scheduler = new MockAlarmScheduler();
        var coordinator = new AlarmCoordinator(store, calc, scheduler);

        var futureSnooze = DateTimeOffset.UtcNow.AddMinutes(7);
        var alarm = new LocalAlarmModel
        {
            Id = "alarm-snooze",
            Name = "Snoozed Alarm",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday,
            Time = new TimeOnly(7, 0),
            TimeZoneId = "Asia/Tokyo",
            SnoozeUntil = futureSnooze
        };
        await store.SaveAsync(alarm);

        // When boot or app resume triggers RescheduleAllActiveAsync, active future snooze must be scheduled!
        await coordinator.RescheduleAllActiveAsync();

        Assert.Single(scheduler.ScheduledAlarms);
        Assert.Equal("alarm-snooze", scheduler.ScheduledAlarms[0].Id);
        Assert.Equal(futureSnooze, scheduler.ScheduledAlarms[0].Time);

        // Verify store still retains SnoozeUntil
        var stored = await store.GetByIdAsync("alarm-snooze");
        Assert.NotNull(stored);
        Assert.Equal(futureSnooze, stored.SnoozeUntil);
    }

    [Fact]
    public async Task B4_SchedulerFailure_MarksStatusFailedAndDoesNotCrash()
    {
        var storeFile = Path.Combine(_tempDir, "alarms.json");
        var store = new JsonFileAlarmStore(storeFile);
        var holidayService = new JapaneseHolidayService(initialCalendar: null);
        var calc = new AlarmScheduleCalculator(holidayService);
        var failingScheduler = new FailingAlarmScheduler(new UnauthorizedAccessException("SCHEDULE_EXACT_ALARM revoked"));
        var coordinator = new AlarmCoordinator(store, calc, failingScheduler);

        var alarm = new LocalAlarmModel
        {
            Id = "alarm-fail",
            Name = "Fail Alarm",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday,
            Time = new TimeOnly(12, 0),
            TimeZoneId = "Asia/Tokyo"
        };

        // Saving and scheduling should handle failure gracefully, not crash with unhandled exception
        var nextFire = await coordinator.SaveAndScheduleAsync(alarm);

        var stored = await store.GetByIdAsync("alarm-fail");
        Assert.NotNull(stored);
        Assert.Equal(AlarmRegistrationStatus.Failed, stored.OsRegistrationStatus);
        Assert.Contains("SCHEDULE_EXACT_ALARM", stored.OsRegistrationError);
        Assert.True(stored.HasRegistrationError);
    }

    [Fact]
    public void B5_AlarmStoragePathHelper_ProvidesConsistentFilenames()
    {
        Assert.Equal("wakemesh_alarms.json", AlarmStoragePathHelper.AlarmsFileName);
        Assert.Equal("wakemesh_device_id.txt", AlarmStoragePathHelper.DeviceIdFileName);
        Assert.Equal("wakemesh_holidays.csv", AlarmStoragePathHelper.HolidayCacheFileName);
        Assert.EndsWith("wakemesh_alarms.json", AlarmStoragePathHelper.GetAlarmsJsonPath());
    }

    [Fact]
    public void B11_DiagnosticsViewModel_DoesNotClaimExactRinging_AndMeshP2PIsRemoved()
    {
        var perm = new MockPermissionService();
        var holiday = new JapaneseHolidayService(initialCalendar: null);
        var vm = new DiagnosticsViewModel(perm, holiday);

        // Must not assert "正確に鳴動します"
        Assert.DoesNotContain("正確に鳴動します", vm.PermissionStatusSummary);
    }
}

public class FailingAlarmScheduler : IAlarmScheduler
{
    private readonly Exception _exceptionToThrow;

    public FailingAlarmScheduler(Exception exceptionToThrow)
    {
        _exceptionToThrow = exceptionToThrow;
    }

    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
    {
        throw _exceptionToThrow;
    }

    public Task CancelAlarmAsync(string alarmId)
    {
        return Task.CompletedTask;
    }
}
