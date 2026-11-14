using System;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class MockPermissionService : IPermissionService
{
    public bool CanScheduleExactAlarms { get; set; } = true;
    public bool HasNotificationPermission { get; set; } = true;
    public bool IsIgnoringBatteryOptimizations { get; set; } = true;
    public bool CanUseFullScreenIntent { get; set; } = true;
    public int OsApiLevel { get; set; } = 35; // Android 15
    public bool RequestNotificationPermissionCalled { get; private set; }
    public bool OpenExactAlarmSettingsCalled { get; private set; }
    public bool OpenFullScreenIntentSettingsCalled { get; private set; }
    public bool OpenBatteryOptimizationSettingsCalled { get; private set; }

    public void RequestNotificationPermission() => RequestNotificationPermissionCalled = true;
    public void OpenExactAlarmSettings() => OpenExactAlarmSettingsCalled = true;
    public void OpenFullScreenIntentSettings() => OpenFullScreenIntentSettingsCalled = true;
    public void OpenBatteryOptimizationSettings() => OpenBatteryOptimizationSettingsCalled = true;
}

public class DiagnosticsViewModelTests
{
    [Fact]
    public void DiagnosticsViewModel_Android15_AccuratelyReportsRequirements()
    {
        var perm = new MockPermissionService { OsApiLevel = 35 };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday);

        Assert.Equal("Android 15 (API 35)", vm.OsVersionText);
        Assert.True(vm.IsExactAlarmGranted);
        Assert.True(vm.IsNotificationGranted);
        Assert.True(vm.IsBatteryOptimizationIgnored);
        Assert.True(vm.IsAllPermissionsHealthy);
    }

    [Fact]
    public void DiagnosticsViewModel_MissingNotification_FlagsWarning()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 34,
            HasNotificationPermission = false
        };
        var holiday = new JapaneseHolidayService();
        var vm = new DiagnosticsViewModel(perm, holiday);

        Assert.False(vm.IsNotificationGranted);
        Assert.False(vm.IsAllPermissionsHealthy);
        Assert.Contains("通知権限", vm.PermissionStatusSummary);
    }

    // T70: the Doze/battery row was display-only (no service method, no command, no button).
    [Fact]
    public async Task Diagnostics_BatteryOptimization_OpensSettingsWhenStillOptimized()
    {
        var perm = new MockPermissionService
        {
            OsApiLevel = 36,
            IsIgnoringBatteryOptimizations = false
        };
        var vm = new DiagnosticsViewModel(perm, new JapaneseHolidayService());

        Assert.Contains("Doze制限の可能性あり", vm.BatteryStatus);
        Assert.True(vm.CanOpenBatteryOptimizationSettings);
        Assert.Equal("省電力除外を設定", vm.BatteryActionText);
        Assert.True(vm.OpenBatteryOptimizationSettingsCommand.CanExecute(null));

        await vm.OpenBatteryOptimizationSettingsCommand.ExecuteAsync(null);

        Assert.True(perm.OpenBatteryOptimizationSettingsCalled);
    }

    [Fact]
    public void Diagnostics_BatteryOptimization_NotOfferedWhenExemptOrUnsupported()
    {
        var exempt = new MockPermissionService { OsApiLevel = 35, IsIgnoringBatteryOptimizations = true };
        var exemptVm = new DiagnosticsViewModel(exempt, new JapaneseHolidayService());
        Assert.False(exemptVm.CanOpenBatteryOptimizationSettings);
        Assert.False(exemptVm.OpenBatteryOptimizationSettingsCommand.CanExecute(null));
        Assert.Equal("最適化除外済み", exemptVm.BatteryActionText);

        var old = new MockPermissionService { OsApiLevel = 22, IsIgnoringBatteryOptimizations = false };
        var oldVm = new DiagnosticsViewModel(old, new JapaneseHolidayService());
        Assert.False(oldVm.CanOpenBatteryOptimizationSettings);
        Assert.Equal("不要 (OS対象外)", oldVm.BatteryActionText);
        Assert.Contains("不要", oldVm.BatteryStatus);
    }
}
