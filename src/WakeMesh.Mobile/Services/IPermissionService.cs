namespace WakeMesh.Mobile.Services;

public interface IPermissionService
{
    bool CanScheduleExactAlarms { get; }
    bool HasNotificationPermission { get; }
    bool IsIgnoringBatteryOptimizations { get; }
    bool CanUseFullScreenIntent { get; }
    int OsApiLevel { get; }

    void RequestNotificationPermission();
    void OpenExactAlarmSettings();
    void OpenFullScreenIntentSettings();
    /// <summary>
    /// T70: navigates to the OS screen that controls this app's Doze / battery-optimization
    /// exemption. Implementations must target the calling package (not the global list) when the
    /// app is still optimized, and fall back to a reachable settings screen when the request
    /// intent is unsupported.
    /// </summary>
    void OpenBatteryOptimizationSettings();
}
