using System;
using System.Threading;
using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;

namespace WakeMesh.Mobile.Platforms.Android.Push;

#pragma warning disable CS0618
[Preserve(AllMembers = true)]
#pragma warning restore CS0618
public class PushSyncWorker : Worker
{
    private readonly CancellationTokenSource _cts = new();

    public PushSyncWorker(Context context, WorkerParameters workerParams)
        : base(context, workerParams)
    {
    }

    public override void OnStopped()
    {
        base.OnStopped();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // DoWork already completed and disposed _cts; safely ignore to prevent worker crash
        }
        catch (Exception)
        {
            // Safe containment on race
        }
    }

    public override Result DoWork()
    {
        var services = PushServiceResolver.ResolveServices();
        if (services == null)
        {
            return Result.InvokeSuccess()!;
        }

        var cloudService = services.GetService<ICloudManagementService>();
        var authService = services.GetService<IAuthService>();

        if (cloudService == null || authService == null)
        {
            return Result.InvokeSuccess()!;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

            var outcome = PushSyncWorkerLogic.ExecuteAsync(cloudService, authService, linkedCts.Token)
                .GetAwaiter()
                .GetResult();

            return outcome switch
            {
                PushSyncWorkerOutcome.Success => Result.InvokeSuccess()!,
                PushSyncWorkerOutcome.Retry => Result.InvokeRetry()!,
                _ => Result.InvokeFailure()!
            };
        }
        catch (OperationCanceledException)
        {
            return Result.InvokeRetry()!;
        }
        catch (Exception)
        {
            return Result.InvokeFailure()!;
        }
        finally
        {
            try
            {
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
