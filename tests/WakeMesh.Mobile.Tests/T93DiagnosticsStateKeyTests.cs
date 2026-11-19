using System;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T93: the diagnostics page marks each permission row with the T80 §4 state key, derived from the
/// same OS gates the status text already uses. Android 12 (API 32) has no runtime notification
/// permission and API 33 no full-screen intent permission, so those rows must read notRequired —
/// not the fixed "●" the page used to draw for every state.
/// </summary>
public sealed class T93DiagnosticsStateKeyTests
{
    private static DiagnosticsViewModel Create(
        int api,
        bool exact = true,
        bool notification = true,
        bool battery = true,
        bool fullScreen = true,
        bool holidays = true)
    {
        var permission = new MockPermissionService
        {
            OsApiLevel = api,
            CanScheduleExactAlarms = exact,
            HasNotificationPermission = notification,
            IsIgnoringBatteryOptimizations = battery,
            CanUseFullScreenIntent = fullScreen
        };
        var holiday = holidays
            ? new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) })
            : JapaneseHolidayService.CreateWithoutData();
        return new DiagnosticsViewModel(permission, holiday);
    }

    [Fact]
    public void AllGatesSatisfied_AllMarksAreOk()
    {
        var vm = Create(36);

        Assert.Equal("ok", vm.ExactAlarmStateKey);
        Assert.Equal("ok", vm.NotificationStateKey);
        Assert.Equal("ok", vm.FullScreenStateKey);
        Assert.Equal("ok", vm.BatteryStateKey);
        Assert.Equal("ok", vm.PermissionSummaryStateKey);
        Assert.Equal("ok", vm.HolidayStateKey);
    }

    [Fact]
    public void Android12_NotificationAndFullScreenAreNotRequired()
    {
        var vm = Create(32, notification: false, fullScreen: false);

        Assert.Equal("notRequired", vm.NotificationStateKey);
        Assert.Equal("notRequired", vm.FullScreenStateKey);
        // API 31/32 still has the exact-alarm permission, and it is granted here.
        Assert.Equal("ok", vm.ExactAlarmStateKey);
    }

    [Fact]
    public void PreMarshmallow_BatteryAndExactAlarmAreNotRequired()
    {
        var vm = Create(22, exact: false, battery: false);

        Assert.Equal("notRequired", vm.ExactAlarmStateKey);
        Assert.Equal("notRequired", vm.BatteryStateKey);
    }

    [Fact]
    public void MissingExactAlarm_IsCriticalOnBothRowAndSummary()
    {
        var vm = Create(34, exact: false);

        Assert.Equal("critical", vm.ExactAlarmStateKey);
        Assert.Equal("critical", vm.PermissionSummaryStateKey);
    }

    [Fact]
    public void MissingNotificationOrBattery_IsWarnAndSummaryStaysWarn()
    {
        var notification = Create(34, notification: false);
        Assert.Equal("warn", notification.NotificationStateKey);
        Assert.Equal("warn", notification.PermissionSummaryStateKey);

        var battery = Create(34, battery: false);
        Assert.Equal("warn", battery.BatteryStateKey);
        Assert.Equal("warn", battery.PermissionSummaryStateKey);
    }

    [Fact]
    public void MissingFullScreenOnApi34_IsWarn()
    {
        var vm = Create(34, fullScreen: false);

        Assert.Equal("warn", vm.FullScreenStateKey);
        Assert.Equal("warn", vm.PermissionSummaryStateKey);
    }

    [Fact]
    public void HolidayDataMissing_IsWarnAndPresentDataIsOk()
    {
        Assert.Equal("warn", Create(36, holidays: false).HolidayStateKey);
        Assert.Equal("ok", Create(36).HolidayStateKey);
    }

    [Fact]
    public void StateKeysAreReadOnly()
    {
        foreach (var name in new[]
                 {
                     nameof(DiagnosticsViewModel.ExactAlarmStateKey),
                     nameof(DiagnosticsViewModel.NotificationStateKey),
                     nameof(DiagnosticsViewModel.FullScreenStateKey),
                     nameof(DiagnosticsViewModel.BatteryStateKey),
                     nameof(DiagnosticsViewModel.PermissionSummaryStateKey),
                     nameof(DiagnosticsViewModel.HolidayStateKey)
                 })
        {
            var property = typeof(DiagnosticsViewModel).GetProperty(name);
            Assert.NotNull(property);
            Assert.False(property!.CanWrite, $"{name} must stay read-only");
        }
    }

    [Fact]
    public void StateKeysOnlyUseTheSpecVocabulary()
    {
        var allowed = new[] { "ok", "pending", "unknown", "warn", "critical", "notRequired", "inactive" };
        var seen = new[]
        {
            Create(36), Create(32, notification: false), Create(34, exact: false),
            Create(34, battery: false), Create(34, fullScreen: false), Create(36, holidays: false)
        };

        foreach (var vm in seen)
        {
            foreach (var key in new[]
                     {
                         vm.ExactAlarmStateKey, vm.NotificationStateKey, vm.FullScreenStateKey,
                         vm.BatteryStateKey, vm.PermissionSummaryStateKey, vm.HolidayStateKey
                     })
            {
                Assert.Contains(key, allowed);
            }
        }
    }
}
