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

    public void RequestNotificationPermission() => RequestNotificationPermissionCalled = true;
    public void OpenExactAlarmSettings() => OpenExactAlarmSettingsCalled = true;
    public void OpenFullScreenIntentSettings() => OpenFullScreenIntentSettingsCalled = true;
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
}
