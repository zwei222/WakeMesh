using System;
using System.Collections.Generic;
using System.ComponentModel;
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
/// T88 / T80 P0-8 + P0-5 + P1-2 + P1-3.
/// Readiness is a display contract: the alarm tab must say what is missing without ever claiming
/// a permission is granted when the OS state is unknown, and the one-tap stop must not be gated by
/// the list reload that runs on every appearance (P0-5). The stop test asserts the real dismiss
/// call happens, so a CanExecute-only fix cannot pass.
/// </summary>
public sealed class T88ReadinessAndStopGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly MockAlarmScheduler _scheduler;
    private readonly LocalDeviceInfoProvider _deviceInfoProvider;

    public T88ReadinessAndStopGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t88_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _scheduler = new MockAlarmScheduler();
        _deviceInfoProvider = new LocalDeviceInfoProvider("t88-device");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private AlarmListViewModel CreateVm(
        IPermissionService? permissions = null,
        IHolidayService? holiday = null,
        IAlarmRingingService? ringing = null)
    {
        var holidayService = holiday ?? new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        var coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(holidayService), _scheduler);
        return new AlarmListViewModel(
            coordinator,
            holidayService,
            _deviceInfoProvider,
            ringing,
            null,
            null,
            permissions);
    }

    // --- P0-8 readiness -------------------------------------------------

    [Fact]
    public void Readiness_AllRequiredPermissionsGranted_IsNoneWithNoBannerText()
    {
        var perm = new MockPermissionService { OsApiLevel = 36 };
        var vm = CreateVm(perm);

        Assert.Equal("None", vm.ReadinessLevel);
        Assert.Equal(string.Empty, vm.ReadinessText);
    }

    [Fact]
    public void Readiness_ExactAlarmMissing_IsCriticalAndNamesTheMissingItem()
    {
        var perm = new MockPermissionService { OsApiLevel = 31, CanScheduleExactAlarms = false };
        var vm = CreateVm(perm);

        Assert.Equal("Critical", vm.ReadinessLevel);
        Assert.Contains("正確なアラーム", vm.ReadinessText);
        Assert.DoesNotContain("許可済み", vm.ReadinessText);
    }

    [Fact]
    public void Readiness_NotificationMissing_IsWarn()
    {
        var perm = new MockPermissionService { OsApiLevel = 33, HasNotificationPermission = false };
        var vm = CreateVm(perm);

        Assert.Equal("Warn", vm.ReadinessLevel);
        Assert.Contains("通知", vm.ReadinessText);
    }

    [Fact]
    public void Readiness_FullScreenMissingOnApi34_IsWarn()
    {
        var perm = new MockPermissionService { OsApiLevel = 34, CanUseFullScreenIntent = false };
        var vm = CreateVm(perm);

        Assert.Equal("Warn", vm.ReadinessLevel);
        Assert.Contains("全画面", vm.ReadinessText);
    }

    [Fact]
    public void Readiness_BatteryExemptionMissing_IsWarn()
    {
        var perm = new MockPermissionService { OsApiLevel = 36, IsIgnoringBatteryOptimizations = false };
        var vm = CreateVm(perm);

        Assert.Equal("Warn", vm.ReadinessLevel);
        Assert.Contains("電池", vm.ReadinessText);
    }

    [Theory]
    [InlineData(30, false, false, false)] // exact alarm is not required below API 31
    [InlineData(32, true, false, false)]  // notification/full screen not required below 33/34
    public void Readiness_UngatedByOsLevel_IsNone(int api, bool exact, bool notification, bool fullScreen)
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = api,
            CanScheduleExactAlarms = exact,
            HasNotificationPermission = notification,
            CanUseFullScreenIntent = fullScreen,
            IsIgnoringBatteryOptimizations = true
        };
        var vm = CreateVm(perm);

        Assert.Equal("None", vm.ReadinessLevel);
    }

    [Fact]
    public void Readiness_WithoutPermissionService_IsNotReportedAsReady()
    {
        var vm = CreateVm(permissions: null);

        // The VM cannot see the OS state: it must not pretend every permission is granted.
        Assert.NotEqual("None", vm.ReadinessLevel);
        Assert.False(string.IsNullOrWhiteSpace(vm.ReadinessText));
        Assert.Contains("確認", vm.ReadinessText);
    }

    [Fact]
    public void Readiness_RefreshReadsCurrentOsStateAndNotifies()
    {
        var perm = new MockPermissionService { OsApiLevel = 36, CanScheduleExactAlarms = false };
        var vm = CreateVm(perm);
        Assert.Equal("Critical", vm.ReadinessLevel);

        var changed = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        perm.CanScheduleExactAlarms = true;
        vm.RefreshReadiness();

        Assert.Equal("None", vm.ReadinessLevel);
        Assert.Contains(nameof(AlarmListViewModel.ReadinessLevel), changed);
        Assert.Contains(nameof(AlarmListViewModel.ReadinessText), changed);
    }

    [Fact]
    public void IsHolidayDataMissing_TracksHolidayServiceState()
    {
        var withoutData = CreateVm(holiday: new JapaneseHolidayService());
        Assert.True(withoutData.IsHolidayDataMissing);

        var withData = CreateVm(holiday: new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) }));
        Assert.False(withData.IsHolidayDataMissing);
    }

    // --- P0-5 one-tap stop must not wait for the list reload ------------

    [Fact]
    public async Task StopAll_WhileListIsLoading_StillStopsTheRingingAlarms()
    {
        var ringing = new StubAlarmRingingService();
        ringing.AddForTesting(new ActiveRingingAlarm("alarm-1", "朝の起床", DateTimeOffset.UtcNow, 5));
        ringing.AddForTesting(new ActiveRingingAlarm("alarm-2", "薬の服用", DateTimeOffset.UtcNow, 3));
        var vm = CreateVm(ringing: ringing);

        vm.IsBusy = true; // a list reload is in flight (every page appearance triggers one)
        Assert.True(vm.IsRinging);
        Assert.True(vm.DismissAllRingingCommand.CanExecute(null));

        await vm.DismissAllRingingCommand.ExecuteAsync(null);

        // The stop must have reached the ringing service, not just been enabled.
        Assert.False(vm.IsRinging);
        Assert.Empty(ringing.GetRingingAlarms());
    }

    [Fact]
    public async Task StopSingle_WhileListIsLoading_StillStopsThatAlarm()
    {
        var ringing = new StubAlarmRingingService();
        ringing.AddForTesting(new ActiveRingingAlarm("alarm-1", "朝の起床", DateTimeOffset.UtcNow, 5));
        ringing.AddForTesting(new ActiveRingingAlarm("alarm-2", "薬の服用", DateTimeOffset.UtcNow, 3));
        var vm = CreateVm(ringing: ringing);

        vm.IsBusy = true;
        Assert.True(vm.DismissRingingAlarmCommand.CanExecute("alarm-1"));

        await vm.DismissRingingAlarmCommand.ExecuteAsync("alarm-1");

        Assert.Single(ringing.GetRingingAlarms());
        Assert.Equal("alarm-2", ringing.GetRingingAlarms()[0].AlarmId);
    }

    // --- P1-2 / P1-3 diagnostics ---------------------------------------

    [Fact]
    public void Diagnostics_AllPermissionsHealthy_IncludesFullScreenOnApi34()
    {
        var missingFullScreen = new MockPermissionService
        {
            OsApiLevel = 34,
            CanUseFullScreenIntent = false
        };
        var vm = new DiagnosticsViewModel(missingFullScreen, new JapaneseHolidayService());

        Assert.False(vm.IsAllPermissionsHealthy);
        Assert.Contains("全画面", vm.PermissionStatusSummary);

        var healthy = new MockPermissionService { OsApiLevel = 34, CanUseFullScreenIntent = true };
        Assert.True(new DiagnosticsViewModel(healthy, new JapaneseHolidayService()).IsAllPermissionsHealthy);
    }

    [Fact]
    public void Diagnostics_AllPermissionsHealthy_IgnoresFullScreenBelowApi34()
    {
        var perm = new MockPermissionService { OsApiLevel = 33, CanUseFullScreenIntent = false };
        var vm = new DiagnosticsViewModel(perm, new JapaneseHolidayService());

        Assert.True(vm.IsAllPermissionsHealthy);
    }

    [Fact]
    public void Diagnostics_IsHolidayDataMissing_TracksHolidayServiceState()
    {
        var perm = new MockPermissionService { OsApiLevel = 36 };

        Assert.True(new DiagnosticsViewModel(perm, new JapaneseHolidayService()).IsHolidayDataMissing);
        Assert.False(new DiagnosticsViewModel(perm, new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) })).IsHolidayDataMissing);
    }

    // --- copy-map: plain words for the holiday / permission notices ----

    [Fact]
    public void AlarmList_HolidayMissing_CopyIsPlainAndKeepsFailOpenMeaning()
    {
        var vm = CreateVm(holiday: new JapaneseHolidayService());

        Assert.Contains("祝日情報が未取得", vm.HolidayStatusText);
        Assert.Contains("祝日もアラーム対象", vm.HolidayStatusText);
        Assert.DoesNotContain("鳴動優先", vm.HolidayStatusText);
    }

    [Fact]
    public void Diagnostics_HolidayMissing_CopyIsPlainAndKeepsFailOpenMeaning()
    {
        var vm = new DiagnosticsViewModel(
            new MockPermissionService { OsApiLevel = 36 },
            new JapaneseHolidayService());

        Assert.Contains("祝日のスキップは適用されません", vm.HolidayCoverageNotice);
        Assert.DoesNotContain("鳴動優先", vm.HolidayCoverageNotice);
    }

    [Fact]
    public void Diagnostics_MissingPermissionSummary_UsesCopyMapWording()
    {
        var vm = new DiagnosticsViewModel(
            new MockPermissionService { OsApiLevel = 34, HasNotificationPermission = false },
            new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) }));

        Assert.Contains("アラームを鳴らすには", vm.PermissionStatusSummary);
        Assert.Contains("通知権限", vm.PermissionStatusSummary);
        Assert.DoesNotContain("確実な鳴動", vm.PermissionStatusSummary);
    }

    [Fact]
    public void AlarmList_RingingBanner_UsesPlainAlarmWording()
    {
        var ringing = new StubAlarmRingingService();
        ringing.AddForTesting(new ActiveRingingAlarm("alarm-1", "薬の服用", DateTimeOffset.UtcNow, 5));
        var vm = CreateVm(ringing: ringing);

        Assert.Contains("薬の服用", vm.RingingBannerTitle);
        Assert.Contains("鳴っています", vm.RingingBannerTitle);
        Assert.DoesNotContain("鳴動", vm.RingingBannerTitle);
    }
}
