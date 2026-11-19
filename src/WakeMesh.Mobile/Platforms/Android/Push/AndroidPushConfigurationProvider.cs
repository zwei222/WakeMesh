using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Gms.Tasks;
using Firebase;
using Firebase.Messaging;

namespace WakeMesh.Mobile.Platforms.Android.Push;

public static class AndroidPushConfigurationProvider
{
    /// <summary>
    /// Checks whether Firebase has been configured in the current Android application.
    /// If google-services.json was not included at build time, FirebaseApp will be uninitialized.
    /// </summary>
    public static bool IsFirebaseConfigured(Context? context)
    {
        if (context == null) return false;

        try
        {
            var apps = FirebaseApp.GetApps(context);
            return apps != null && apps.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely retrieves initial FCM token if Firebase is configured.
    /// If Firebase is not configured (network 0 / local only), returns null immediately without throwing.
    /// </summary>
    public static async Task<string?> TryFetchInitialFcmTokenAsync(Context? context)
    {
        if (!IsFirebaseConfigured(context))
        {
            return null;
        }

        try
        {
            var instance = FirebaseMessaging.Instance;
            if (instance == null) return null;

#pragma warning disable CS0618
            var task = instance.GetToken();
#pragma warning restore CS0618
            var tcs = new TaskCompletionSource<string?>();
            task.AddOnCompleteListener(new FcmTokenCompleteListener(tcs));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private sealed class FcmTokenCompleteListener : Java.Lang.Object, IOnCompleteListener
    {
        private readonly TaskCompletionSource<string?> _tcs;

        public FcmTokenCompleteListener(TaskCompletionSource<string?> tcs)
        {
            _tcs = tcs;
        }

        public void OnComplete(global::Android.Gms.Tasks.Task task)
        {
            if (task.IsSuccessful && task.Result != null)
            {
                _tcs.TrySetResult(task.Result.ToString());
            }
            else
            {
                _tcs.TrySetResult(null);
            }
        }
    }
}
