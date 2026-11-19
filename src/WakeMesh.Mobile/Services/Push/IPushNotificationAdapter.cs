using System;

namespace WakeMesh.Mobile.Services.Push;

public interface IPushNotificationAdapter
{
    bool HasNotificationPermission { get; }

    event EventHandler? NotificationPermissionChanged;

    /// <summary>
    /// Displays immediate user-visible notification:
    /// "設定変更: アラーム設定の変更を受信しました"
    /// Personal alarm label and alarm times are excluded.
    /// Notification sound is silent / not required.
    /// </summary>
    void ShowSettingsChangedNotification();
}
