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
}
