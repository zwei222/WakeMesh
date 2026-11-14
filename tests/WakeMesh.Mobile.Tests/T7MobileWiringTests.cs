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

public class T7MobileWiringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly MockAlarmScheduler _scheduler;
    private readonly LocalDeviceInfoProvider _deviceInfoProvider;

    public T7MobileWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t7_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _scheduler = new MockAlarmScheduler();
        _deviceInfoProvider = new LocalDeviceInfoProvider("test-device");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    // --- 1. Diagnostics Wiring & OS Exclusions & Re-registration & Double-tap Prevention ---

    [Fact]
    public async Task Diagnostics_PostNotifications_CallsPermissionServiceOnApi33Plus()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 33,
            HasNotificationPermission = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.True(vm.CanRequestNotification);
        Assert.True(vm.RequestNotificationCommand.CanExecute(null));

        await vm.RequestNotificationCommand.ExecuteAsync(null);

        Assert.True(perm.RequestNotificationPermissionCalled);
    }

    [Fact]
    public void Diagnostics_PostNotifications_ReportsNotRequiredOnApiUnder33()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 32, // Android 12L
            HasNotificationPermission = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.False(vm.CanRequestNotification);
        Assert.False(vm.RequestNotificationCommand.CanExecute(null));
        Assert.Contains("不要", vm.NotificationStatus);
    }

    [Fact]
    public async Task Diagnostics_ExactAlarm_CallsSettingsOnApi31Plus()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 31,
            CanScheduleExactAlarms = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.True(vm.CanOpenExactAlarmSettings);
        Assert.True(vm.OpenExactAlarmSettingsCommand.CanExecute(null));

        await vm.OpenExactAlarmSettingsCommand.ExecuteAsync(null);

        Assert.True(perm.OpenExactAlarmSettingsCalled);
    }

    [Fact]
    public void Diagnostics_ExactAlarm_ReportsNotRequiredOnApiUnder31()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 30, // Android 11
            CanScheduleExactAlarms = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.False(vm.CanOpenExactAlarmSettings);
        Assert.False(vm.OpenExactAlarmSettingsCommand.CanExecute(null));
        Assert.Contains("不要", vm.ExactAlarmStatus);
    }

    [Fact]
    public async Task Diagnostics_FullScreen_CallsSettingsOnApi34Plus()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 34,
            CanUseFullScreenIntent = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.True(vm.CanOpenFullScreenSettings);
        Assert.True(vm.OpenFullScreenSettingsCommand.CanExecute(null));

        await vm.OpenFullScreenSettingsCommand.ExecuteAsync(null);

        Assert.True(perm.OpenFullScreenIntentSettingsCalled);
    }

    [Fact]
    public void Diagnostics_FullScreen_ReportsNotRequiredOnApiUnder34()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 33, // Android 13
            CanUseFullScreenIntent = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.False(vm.CanOpenFullScreenSettings);
        Assert.False(vm.OpenFullScreenSettingsCommand.CanExecute(null));
        Assert.Contains("不要", vm.FullScreenStatus);
    }

    [Fact]
    public async Task Diagnostics_ReDiagnoseOnReturn_ReschedulesActiveAlarms()
    {
        var holiday = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);

        // Seed an active alarm
        await coordinator.SaveAlarmAsync(new LocalAlarmModel
        {
            Name = "朝のアラーム",
            Time = new TimeOnly(7, 30),
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday
        });

        _scheduler.ScheduledAlarms.Clear();

        var perm = new MockPermissionService { OsApiLevel = 34 };
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider, coordinator);

        await vm.RefreshCommand.ExecuteAsync(null);

        // Active alarm should be re-registered with OS
        Assert.Single(_scheduler.ScheduledAlarms);
        Assert.NotNull(vm.RescheduledMessage);
        Assert.Contains("1", vm.RescheduledMessage);
    }

    [Fact]
    public void Diagnostics_IsBusy_PreventsDoubleExecution()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 34,
            HasNotificationPermission = false,
            CanScheduleExactAlarms = false,
            CanUseFullScreenIntent = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.True(vm.RequestNotificationCommand.CanExecute(null));
        Assert.True(vm.OpenExactAlarmSettingsCommand.CanExecute(null));
        Assert.True(vm.OpenFullScreenSettingsCommand.CanExecute(null));

        vm.IsBusy = true;

        Assert.False(vm.RequestNotificationCommand.CanExecute(null));
        Assert.False(vm.OpenExactAlarmSettingsCommand.CanExecute(null));
        Assert.False(vm.OpenFullScreenSettingsCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    // --- 2. Ringing Banner & Bidirectional Dismiss (Individual / All) ---

    [Fact]
    public async Task AlarmListViewModel_RingingBanner_ReflectsActiveAlarmsAndDismissesAccurately()
    {
        var holiday = new JapaneseHolidayService();
        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);
        var ringingService = new StubAlarmRingingService();

        var vm = new AlarmListViewModel(coordinator, holiday, _deviceInfoProvider, ringingService);

        Assert.False(vm.IsRinging);
        Assert.Empty(vm.RingingAlarms);

        // Add 2 active ringing alarms
        var alarm1 = new ActiveRingingAlarm("alarm-1", "朝の起床", DateTimeOffset.UtcNow, 5);
        var alarm2 = new ActiveRingingAlarm("alarm-2", "薬の服用", DateTimeOffset.UtcNow.AddSeconds(10), 10);
        ringingService.AddForTesting(alarm1);
        ringingService.AddForTesting(alarm2);

        Assert.True(vm.IsRinging);
        Assert.Equal(2, vm.RingingAlarms.Count);
        Assert.Contains("2 件", vm.RingingBannerTitle);

        // Dismiss single alarm
        await vm.DismissRingingAlarmCommand.ExecuteAsync("alarm-1");

        Assert.True(vm.IsRinging);
        Assert.Single(vm.RingingAlarms);
        Assert.Equal("alarm-2", vm.RingingAlarms[0].AlarmId);
        // T88 copy-map: 鳴動中 → のアラームが鳴っています (the fact is unchanged)
        Assert.Contains("薬の服用 のアラームが鳴っています", vm.RingingBannerTitle);

        // Dismiss all
        await vm.DismissAllRingingCommand.ExecuteAsync(null);

        Assert.False(vm.IsRinging);
        Assert.Empty(vm.RingingAlarms);
    }

    [Fact]
    public async Task AlarmListViewModel_DismissNonExistentAlarm_DoesNotReportFalseSuccess()
    {
        var holiday = new JapaneseHolidayService();
        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);
        var ringingService = new StubAlarmRingingService();

        var vm = new AlarmListViewModel(coordinator, holiday, _deviceInfoProvider, ringingService);

        var result = await ringingService.DismissAlarmAsync("non-existent-id");
        Assert.False(result);

        var dismissAllResult = await ringingService.DismissAllAsync();
        Assert.False(dismissAllResult);
    }

    // --- 3. Holiday Refresh, Lifecycle Auto-refresh, and Honest Coverage Warning ---

    [Fact]
    public async Task Diagnostics_RefreshHolidays_OnFailure_RetainsExistingCacheAndReportsReason()
    {
        var cacheFile = Path.Combine(_tempDir, "holiday_cache.csv");
        // Create an existing valid cache
        var initialDates = new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 12) };
        var holiday = new JapaneseHolidayService(holidays: initialDates, cacheFilePath: cacheFile);

        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);
        var perm = new MockPermissionService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider, coordinator);

        // Set failure warning
        var failureHoliday = new FailingHolidayService(initialDates, "503 サービス停止中");
        var vmWithFailure = new DiagnosticsViewModel(perm, failureHoliday, _deviceInfoProvider, coordinator);

        await vmWithFailure.RefreshHolidaysCommand.ExecuteAsync(null);

        Assert.Contains("503 サービス停止中", vmWithFailure.HolidayOperationMessage);
        Assert.True(failureHoliday.HasHolidayData); // Existing cache retained!
    }

    [Fact]
    public void HolidayStatus_HonestCoverageConstraint_WarnsAboutYearEndBeyondFinalHoliday()
    {
        var dates = new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 23) };
        var holiday = new JapaneseHolidayService(holidays: dates);
        var perm = new MockPermissionService();
        var vm = new DiagnosticsViewModel(perm, holiday, _deviceInfoProvider);

        Assert.Contains("2026/01/01", vm.HolidayCoverageNotice);
        Assert.Contains("2026/11/23", vm.HolidayCoverageNotice);
        Assert.Contains("カバレッジ外", vm.HolidayCoverageNotice);
        // T88 copy-map: 鳴動優先 → 祝日もアラーム対象 (fail-open is still stated)
        Assert.Contains("祝日もアラーム対象", vm.HolidayCoverageNotice);
    }

    [Fact]
    public async Task HolidayForegroundSyncCoordinator_ReschedulesAlarmsOnlyOnSuccess()
    {
        var holiday = new MockRefreshableHolidayService { NeedsRefresh = true, RefreshResult = true };
        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);

        // Seed an alarm
        await coordinator.SaveAlarmAsync(new LocalAlarmModel
        {
            Name = "休日除外アラーム",
            Time = new TimeOnly(8, 0),
            IsEnabled = true,
            ExcludeHolidays = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday
        });

        _scheduler.ScheduledAlarms.Clear();

        var syncCoordinator = new HolidayForegroundSyncCoordinator(holiday, coordinator);
        var refreshed = await syncCoordinator.SyncNowAsync();

        Assert.True(refreshed);
        Assert.Single(_scheduler.ScheduledAlarms); // Rescheduled on success!

        // When refresh fails
        holiday.RefreshResult = false;
        _scheduler.ScheduledAlarms.Clear();

        var failedRefresh = await syncCoordinator.SyncNowAsync();
        Assert.False(failedRefresh);
        Assert.Empty(_scheduler.ScheduledAlarms); // NOT rescheduled on failure!
    }

    // --- 4. Sound Options, Volume, and Vibration Wiring ---

    [Fact]
    public void AlarmEditViewModel_SoundOptions_ProvidesOsDefaultAndBuiltinChime()
    {
        var holiday = new JapaneseHolidayService();
        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);
        var vm = new AlarmEditViewModel(coordinator, _deviceInfoProvider);

        Assert.NotNull(vm.SoundOptions);
        Assert.True(vm.SoundOptions.Count >= 2);
        Assert.Contains(vm.SoundOptions, s => s.Id == SoundOption.DefaultOsId);
        Assert.Contains(vm.SoundOptions, s => s.Id == SoundOption.BuiltinChimeId);
        Assert.Equal(SoundOption.DefaultOsId, vm.SelectedSound.Id);
    }

    [Fact]
    public async Task AlarmEditViewModel_PreservesSoundVolumeVibrationOnSaveAndLoad()
    {
        var holiday = new JapaneseHolidayService();
        var calc = new AlarmScheduleCalculator(holiday);
        var coordinator = new AlarmCoordinator(_store, calc, _scheduler);

        var editVm = new AlarmEditViewModel(coordinator, _deviceInfoProvider);
        editVm.Name = "チャイム音テスト";
        editVm.Time = new TimeOnly(7, 15);
        editVm.VolumePercent = 75;
        editVm.EnableVibration = false;
        editVm.SelectedSound = SoundOption.BuiltinChime;

        var saved = await editVm.SaveAlarmAsync();
        Assert.True(saved);

        var loaded = await _store.GetByIdAsync(editVm.Id);
        Assert.NotNull(loaded);
        Assert.Equal(75, loaded.VolumePercent);
        Assert.False(loaded.EnableVibration);
        Assert.Equal(SoundOption.BuiltinChime.Uri, loaded.SoundUri);

        // Load into another edit VM instance
        var loadVm = new AlarmEditViewModel(coordinator, _deviceInfoProvider);
        loadVm.LoadAlarm(loaded);

        Assert.Equal(75, loadVm.VolumePercent);
        Assert.False(loadVm.EnableVibration);
        Assert.Equal(SoundOption.BuiltinChimeId, loadVm.SelectedSound.Id);
    }

    [Theory]
    [InlineData(null, SoundOption.DefaultOsId)]
    [InlineData("", SoundOption.DefaultOsId)]
    [InlineData("wakemesh://sound/builtin_chime", SoundOption.BuiltinChimeId)]
    [InlineData("builtin_chime", SoundOption.BuiltinChimeId)]
    [InlineData("unknown_custom_file", SoundOption.DefaultOsId)]
    public void SoundOption_FromUri_ResolvesExpectedOption(string? uri, string expectedId)
    {
        var option = SoundOption.FromUri(uri);
        Assert.Equal(expectedId, option.Id);
    }
}

// Helper test mocks

public class FailingHolidayService : IHolidayService
{
    private readonly HashSet<DateOnly> _dates;
    public FailingHolidayService(IEnumerable<DateOnly> dates, string warning)
    {
        _dates = dates.ToHashSet();
        DataWarning = warning;
        Calendar = new HolidayCalendar
        {
            RetrievedAt = DateTimeOffset.UtcNow,
            VersionLabel = "Test",
            CoverageStartInclusive = _dates.Min(),
            CoverageEndInclusive = _dates.Max(),
            Dates = _dates
        };
    }

    public bool HasHolidayData => true;
    public string? DataWarning { get; }
    public HolidayCalendar? Calendar { get; }
    public string HolidayStatusSummary => "既存データ維持";
    public bool NeedsRefresh => true;
    public IReadOnlySet<DateOnly> GetHolidays() => _dates;
    public Task<bool> RefreshHolidaysAsync() => Task.FromResult(false);
}

public class MockRefreshableHolidayService : IHolidayService
{
    public bool HasHolidayData => true;
    public string? DataWarning => null;
    public HolidayCalendar? Calendar { get; } = null;
    public string HolidayStatusSummary => "Mock Holiday";
    public bool NeedsRefresh { get; set; } = true;
    public bool RefreshResult { get; set; } = true;

    public IReadOnlySet<DateOnly> GetHolidays() => new HashSet<DateOnly>();
    public Task<bool> RefreshHolidaysAsync() => Task.FromResult(RefreshResult);
}
