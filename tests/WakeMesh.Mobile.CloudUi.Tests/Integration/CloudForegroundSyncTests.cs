using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

public sealed class CloudForegroundSyncTests : IClassFixture<CloudUiTestApiFactory>
{
    private readonly CloudUiTestApiFactory _api;

    public CloudForegroundSyncTests(CloudUiTestApiFactory api)
    {
        _api = api;
    }

    private static string NewDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "fg-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static AlarmCoordinator Coordinator(string dir) =>
        new(new JsonFileAlarmStore(Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var spinner = new SpinWait();
        while (!predicate())
        {
            if (spinner.NextSpinWillYield) await Task.Yield();
            else spinner.SpinOnce();
        }
    }

    [Fact]
    public void DefaultSyncInterval_IsFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), CloudForegroundSyncService.DefaultSyncInterval);
    }

    [Fact]
    public async Task TriggerSyncAsync_ZeroNetwork_WhenNotConfiguredOrSignedIn()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", "token");
        auth.SetState(AuthState.NotConfigured);
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, null, Coordinator(dir), customStorageDir: dir);
        using var fgSync = new CloudForegroundSyncService(service, auth);

        fgSync.Resume();
        Assert.True(fgSync.IsInForeground);

        var outcome = await fgSync.TriggerSyncAsync(CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task TriggerSyncAsync_ZeroNetwork_WhenDeviceNotRegistered()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", _api.Auth.CreateAccessToken());
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, null, Coordinator(dir), customStorageDir: dir);
        using var fgSync = new CloudForegroundSyncService(service, auth);

        fgSync.Resume();

        var outcome = await fgSync.TriggerSyncAsync(CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task TriggerSyncAsync_ExecutesSync_WhenConfiguredSignedInAndRegistered()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", _api.Auth.CreateAccessToken());
        var creds = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => auth.CurrentBinding);
        var client = TestClientHelper.CreateApiClient(_api.CreateClient(), auth, creds);
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, creds, Coordinator(dir), () => client, dir);

        var regResult = await service.RegisterCurrentDeviceAsync("fg-sync-dev");
        Assert.True(regResult.IsSuccess);

        // Resume() auto-starts PeriodicSyncLoop which runs an initial TriggerSync before delayFunc.
        // Park the loop on infinite delay after that initial sync so the manual trigger does not race
        // the gate held by the auto-started sync (WaitAsync(0) would otherwise return null).
        var initialSyncCompleted = NewSignal();
        var parked = NewSignal();
        service.SyncCompleted += (_, _) => initialSyncCompleted.TrySetResult();

        using var fgSync = new CloudForegroundSyncService(
            service,
            auth,
            delayFunc: (_, ct) =>
            {
                parked.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct);
            });

        fgSync.Resume();

        await initialSyncCompleted.Task;
        await parked.Task;
        await WaitForAsync(() => !fgSync.IsSyncInProgress);

        var outcome = await fgSync.TriggerSyncAsync(CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, outcome!.Status);
    }

    [Fact]
    public async Task PeriodicSyncLoop_FiresWithInjectedTimer_AndStopsOnPause()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", _api.Auth.CreateAccessToken());
        var creds = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => auth.CurrentBinding);
        var client = TestClientHelper.CreateApiClient(_api.CreateClient(), auth, creds);
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, creds, Coordinator(dir), () => client, dir);

        var regResult = await service.RegisterCurrentDeviceAsync("fg-timer-dev");
        Assert.True(regResult.IsSuccess);

        var initialSync = NewSignal();
        var loopDelay = NewSignal();
        service.SyncCompleted += (_, _) => initialSync.TrySetResult();

        async Task FastDelay(TimeSpan _, CancellationToken ct)
        {
            await loopDelay.Task.WaitAsync(ct);
        }

        using var fgSync = new CloudForegroundSyncService(
            service,
            auth,
            syncInterval: TimeSpan.FromMilliseconds(50),
            delayFunc: FastDelay);

        fgSync.Resume();
        Assert.True(fgSync.IsInForeground);
        Assert.True(fgSync.IsRunning);

        // The foreground service performs its initial synchronization before waiting for the next tick.
        await initialSync.Task;
        await WaitForAsync(() => fgSync.IsRunning);

        // Release exactly one timer tick and observe the next real sync completion.
        var periodicSync = NewSignal();
        service.SyncCompleted += (_, _) => periodicSync.TrySetResult();
        loopDelay.TrySetResult();
        await periodicSync.Task;

        // Pause cancels its active timer loop.
        fgSync.Pause();
        Assert.False(fgSync.IsInForeground);
        await WaitForAsync(() => !fgSync.IsRunning);
    }

    /// <summary>
    /// Real-API cancel-aware handler: when Armed, parks the in-flight HTTP call on ct so auth
    /// StateChanged cancellation is observed on the wire. Transport is injected via the tests-friend
    /// CreateDefaultClient/CreateForTests path only (no production TLS/auth bypass).
    /// </summary>
    private sealed class CancelAwareBlockingHandler : DelegatingHandler
    {
        public volatile bool Armed;
        public readonly TaskCompletionSource Entered = NewSignal();
        public readonly TaskCompletionSource Canceled = NewSignal();
        private int _requestsAfterCancel;

        public int RequestsAfterCancel => Volatile.Read(ref _requestsAfterCancel);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Canceled.Task.IsCompleted)
            {
                Interlocked.Increment(ref _requestsAfterCancel);
            }

            if (Armed)
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Canceled.TrySetResult();
                    throw;
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task SettingsChangeOrSignOut_CancelsInFlightForegroundSync()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", _api.Auth.CreateAccessToken());
        var creds = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => auth.CurrentBinding);
        var handler = new CancelAwareBlockingHandler();
        // WebApplicationFactory.CreateDefaultClient(handler) is the tests-friend transport injection seam.
        var client = TestClientHelper.CreateApiClient(_api.CreateDefaultClient(handler), auth, creds);
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, creds, Coordinator(dir), () => client, dir);

        var regResult = await service.RegisterCurrentDeviceAsync("fg-cancel-dev");
        Assert.True(regResult.IsSuccess);

        // Arm after register so only the foreground in-flight sync is parked on the wire.
        handler.Armed = true;
        var parked = NewSignal();
        using var fgSync = new CloudForegroundSyncService(
            service,
            auth,
            delayFunc: (_, ct) =>
            {
                parked.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct);
            });

        fgSync.Resume();
        await handler.Entered.Task;
        Assert.True(fgSync.IsSyncInProgress);

        // Sign out / settings change cancels the linked in-flight CTS.
        auth.SetState(AuthState.SignedOut);

        await handler.Canceled.Task;
        await parked.Task;
        await WaitForAsync(() => !fgSync.IsSyncInProgress);

        Assert.False(fgSync.IsSyncInProgress);
        Assert.Equal(0, handler.RequestsAfterCancel);
    }

    [Fact]
    public void NativeAbsence_IsDistinguished()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", "token");
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, null, Coordinator(dir), customStorageDir: dir);
        using var fgSync = new CloudForegroundSyncService(service, auth);

        // Running in unit test runner (no native Android Window attached)
        Assert.False(fgSync.HasNativeWindowAttached);
    }

    [Fact]
    public void RealMauiWindow_Attach_Activated_Deactivated_Activated_CyclesCorrectly()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", "token");
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, null, Coordinator(dir), customStorageDir: dir);
        using var fgSync = new CloudForegroundSyncService(service, auth);

        var window = new Microsoft.Maui.Controls.Window(new Microsoft.Maui.Controls.ContentPage());
        fgSync.AttachWindow(window);

        Assert.True(fgSync.HasNativeWindowAttached);
        Assert.True(fgSync.IsInForeground);
        Assert.True(fgSync.IsRunning);

        var iWindow = (Microsoft.Maui.IWindow)window;

        // Lifecycle flow: Created -> Activated -> Deactivated -> Activated
        iWindow.Created();
        iWindow.Activated();
        Assert.True(fgSync.IsInForeground);

        // Deactivated -> Pauses foreground sync
        iWindow.Deactivated();
        Assert.False(fgSync.IsInForeground);

        // Activated -> Resumes foreground sync
        iWindow.Activated();
        Assert.True(fgSync.IsInForeground);
        Assert.True(fgSync.IsRunning);

        // Symmetric background transition: Deactivated -> Stopped -> Pauses
        iWindow.Deactivated();
        iWindow.Stopped();
        Assert.False(fgSync.IsInForeground);

        // Symmetric resume transition: Resumed -> Activated -> Resumes
        iWindow.Resumed();
        iWindow.Activated();
        Assert.True(fgSync.IsInForeground);
        Assert.True(fgSync.IsRunning);

        // Symmetrical Destroying -> Detaches and Pauses
        iWindow.Deactivated();
        iWindow.Stopped();
        iWindow.Destroying();
        Assert.False(fgSync.IsInForeground);
        Assert.False(fgSync.HasNativeWindowAttached);
    }

    [Fact]
    public void Resume_WhenCalledRepeatedly_DoesNotSpawnDuplicateTimers()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", "token");
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, null, Coordinator(dir), customStorageDir: dir);
        using var fgSync = new CloudForegroundSyncService(service, auth);

        fgSync.Resume();
        Assert.True(fgSync.IsRunning);

        // Multiple calls in sequence
        fgSync.Resume();
        fgSync.Resume();
        Assert.True(fgSync.IsRunning);

        fgSync.Pause();
        Assert.False(fgSync.IsInForeground);
    }
}
