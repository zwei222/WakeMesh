using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Firebase.Messaging;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Mobile.Services.Push;

namespace WakeMesh.Mobile.Platforms.Android.Push;

[Service(Name = "wakemesh.mobile.platforms.android.push.WakeMeshFirebaseMessagingService", Exported = false)]
[IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
public sealed class WakeMeshFirebaseMessagingService : FirebaseMessagingService
{
#pragma warning disable CS0618, CS0672
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        // Token logging is strictly forbidden.
        try
        {
            var pushManager = PushServiceResolver.ResolveServices()?.GetService<IPushManager>();
            if (pushManager != null)
            {
                pushManager.OnNewTokenReceivedAsync(token).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Safe error containment
        }
    }
#pragma warning restore CS0618, CS0672

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);

        if (message == null)
        {
            return;
        }

        // Payload and credentials logging is strictly forbidden.
        // Immediate user notification and WorkManager enqueue execute synchronously in onMessageReceived;
        // long HTTP synchronization is deferred exclusively to the background PushSyncWorker.
        var data = message.Data != null ? new System.Collections.Generic.Dictionary<string, string>(message.Data) : null;
        try
        {
            var pushManager = PushServiceResolver.ResolveServices()?.GetService<IPushManager>();
            if (pushManager != null)
            {
                pushManager.OnPushMessageReceivedAsync(data).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Safe error containment
        }
    }
}
