namespace WakeMesh.Mobile.Services.Push;

public interface IWorkManagerAdapter
{
    /// <summary>
    /// Enqueues background sync work in WorkManager so HTTP requests
    /// are not awaited inside the broadcast receiver / push service.
    /// </summary>
    void EnqueuePushSyncWork();
}
