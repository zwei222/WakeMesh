using System;

namespace WakeMesh.Mobile.Services.Push;

public sealed class StubPushNotificationAdapter : IPushNotificationAdapter
{
    private bool _hasNotificationPermission = true;

    public bool HasNotificationPermission
    {
        get => _hasNotificationPermission;
        set
        {
            if (_hasNotificationPermission != value)
            {
                _hasNotificationPermission = value;
                NotificationPermissionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? NotificationPermissionChanged;
    public int NotificationShownCount { get; private set; }

    public void ShowSettingsChangedNotification()
    {
        NotificationShownCount++;
    }

    public void Reset()
    {
        NotificationShownCount = 0;
    }
}

public sealed class StubWorkManagerAdapter : IWorkManagerAdapter
{
    public int EnqueueCount { get; private set; }

    public void EnqueuePushSyncWork()
    {
        EnqueueCount++;
    }

    public void Reset()
    {
        EnqueueCount = 0;
    }
}
