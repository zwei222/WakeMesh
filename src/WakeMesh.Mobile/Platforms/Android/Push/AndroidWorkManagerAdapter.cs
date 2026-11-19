using System;
using Android.Content;
using AndroidX.Work;
using WakeMesh.Mobile.Services.Push;

namespace WakeMesh.Mobile.Platforms.Android.Push;

public sealed class AndroidWorkManagerAdapter : IWorkManagerAdapter
{
    private readonly Context _context;

    public AndroidWorkManagerAdapter(Context? context = null)
    {
        _context = context ?? global::Android.App.Application.Context;
    }

    public void EnqueuePushSyncWork()
    {
        try
        {
            var constraints = new Constraints.Builder()
                .SetRequiredNetworkType(NetworkType.Connected!)
                .Build();

            var workRequest = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(PushSyncWorker))!)
                .SetConstraints(constraints)
                .Build();

            WorkManager.GetInstance(_context).EnqueueUniqueWork(
                "wakemesh_push_sync",
                ExistingWorkPolicy.Replace!,
                (OneTimeWorkRequest)workRequest);
        }
        catch
        {
            // Fail safely without crashing receiver
        }
    }
}
