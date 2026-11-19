using System;
using System.Reflection;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T70 Doze red/green probe. Battery-optimization (Doze exemption) had no navigable
/// entry point at ANY layer: no IPermissionService method, no VM command, no XAML button
/// (T67 uiautomator dump of the shipped APK shows a Label-only row). These assertions are
/// reflection-based so the probe can be RED before the members exist and GREEN afterwards.
/// </summary>
public class T70DozeEntryPointProbe
{
    [Fact]
    public void T70_PermissionService_ExposesBatteryOptimizationSettingsNavigation()
    {
        var method = typeof(IPermissionService).GetMethod(
            "OpenBatteryOptimizationSettings", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(method);
    }

    [Fact]
    public void T70_ViewModel_ExposesBatteryOptimizationCommand_AndGating()
    {
        var command = typeof(DiagnosticsViewModel).GetProperty("OpenBatteryOptimizationSettingsCommand");
        Assert.NotNull(command);
        Assert.True(typeof(AsyncRelayCommand).IsAssignableFrom(command!.PropertyType));

        var canOpen = typeof(DiagnosticsViewModel).GetProperty("CanOpenBatteryOptimizationSettings");
        Assert.NotNull(canOpen);
        Assert.Equal(typeof(bool), canOpen!.PropertyType);

        var actionText = typeof(DiagnosticsViewModel).GetProperty("BatteryActionText");
        Assert.NotNull(actionText);
        Assert.Equal(typeof(string), actionText!.PropertyType);
    }
}
