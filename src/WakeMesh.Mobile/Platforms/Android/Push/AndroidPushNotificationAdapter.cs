using System;
using Android.App;
using Android.Content;
using AndroidX.Core.App;
using WakeMesh.Mobile.Services.Push;

namespace WakeMesh.Mobile.Platforms.Android.Push;

public sealed class AndroidPushNotificationAdapter : IPushNotificationAdapter
{
    public const string ChannelId = "wakemesh_settings";
    public const int NotificationId = 2001;

    private readonly Context _context;

    public AndroidPushNotificationAdapter(Context? context = null)
    {
        _context = context ?? global::Android.App.Application.Context;
        EnsureNotificationChannel();
    }

    public bool HasNotificationPermission
    {
        get
        {
            var compat = NotificationManagerCompat.From(_context);
            return compat != null && compat.AreNotificationsEnabled();
        }
    }

    public event EventHandler? NotificationPermissionChanged;

    public void NotifyPermissionChanged()
    {
        NotificationPermissionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowSettingsChangedNotification()
    {
        if (!HasNotificationPermission)
        {
            return;
        }

        try
        {
            EnsureNotificationChannel();

            var builder = new NotificationCompat.Builder(_context, ChannelId);
            builder.SetContentTitle("設定変更");
            builder.SetContentText("アラーム設定の変更を受信しました");
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo);
            builder.SetPriority(NotificationCompat.PriorityDefault);
            builder.SetAutoCancel(true);
            builder.SetSound(null);

            var manager = NotificationManagerCompat.From(_context);
            if (manager != null)
            {
                var notification = builder.Build();
                if (notification != null)
                {
                    manager.Notify(NotificationId, notification);
                }
            }
        }
        catch
        {
            // Do not fail push reception if notification display fails
        }
    }

    private void EnsureNotificationChannel()
    {
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            try
            {
                var channel = new NotificationChannel(
                    ChannelId,
                    "設定変更通知",
                    NotificationImportance.Default)
                {
                    Description = "クラウドからの設定更新通知"
                };

                // Notification sound is disabled / not required
                channel.SetSound(null, null);

                var manager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
                manager?.CreateNotificationChannel(channel);
            }
            catch
            {
                // Safe containment
            }
        }
    }
}
