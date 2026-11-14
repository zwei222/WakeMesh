using System;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Sync;

#if ANDROID || IOS || MACCATALYST || WINDOWS
using Microsoft.Maui.Controls;
#endif

namespace WakeMesh.Mobile.Services.CloudManagement;

public interface ICloudForegroundSyncService : IDisposable
{
    bool IsRunning { get; }
    bool IsInForeground { get; }
    bool IsSyncInProgress { get; }
    bool HasNativeWindowAttached { get; }

#if ANDROID || IOS || MACCATALYST || WINDOWS
    void AttachWindow(Window window);
#endif
    void OnAppResumed();
    void OnAppPaused();
    void Pause();
    void Resume();
    Task<DesiredSyncOutcome?> TriggerSyncAsync(CancellationToken cancellationToken = default);
}
