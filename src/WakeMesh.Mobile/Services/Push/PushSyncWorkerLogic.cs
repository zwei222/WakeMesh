using System;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;

namespace WakeMesh.Mobile.Services.Push;

public enum PushSyncWorkerOutcome
{
    Success,
    Retry,
    Failure
}

public static class PushSyncWorkerLogic
{
    public static async Task<PushSyncWorkerOutcome> ExecuteAsync(
        ICloudManagementService cloudService,
        IAuthService authService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloudService);
        ArgumentNullException.ThrowIfNull(authService);

        // 1. Worker awaits Auth.InitializeAsync before evaluating state
        await authService.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // 2. State evaluation
        // Silent authentication only: if interactive login is required, NEVER trigger interactive UI in background!
        // Return retry without opening UI, waiting for user re-login
        if (authService.State == AuthState.ReauthRequired)
        {
            return PushSyncWorkerOutcome.Retry;
        }

        // If not configured, zero network requests and successDisabled
        if (!cloudService.IsConfiguredAndSignedIn || authService.State == AuthState.NotConfigured)
        {
            return PushSyncWorkerOutcome.Success;
        }

        try
        {
            var outcome = await cloudService.SyncCurrentDeviceAsync(cancellationToken).ConfigureAwait(false);

            if (outcome.Status == DesiredSyncStatus.AppliedAcknowledged || outcome.Status == DesiredSyncStatus.StaleRevisionSkipped)
            {
                return PushSyncWorkerOutcome.Success;
            }

            if (outcome.Status == DesiredSyncStatus.TransportFailed)
            {
                return PushSyncWorkerOutcome.Retry;
            }

            return PushSyncWorkerOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            return PushSyncWorkerOutcome.Retry;
        }
        catch
        {
            return PushSyncWorkerOutcome.Failure;
        }
    }
}
