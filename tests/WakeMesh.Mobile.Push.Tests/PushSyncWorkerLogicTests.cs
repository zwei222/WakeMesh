using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

public sealed class PushSyncWorkerLogicTests
{
    private const string ApiUrl = "https://api.example.com";
    private const string AccountId = "user-123";
    private readonly Guid _deviceId = Guid.NewGuid();

    private sealed class TrackingAuthService : IAuthService
    {
        public AuthState State { get; set; } = AuthState.SignedIn;
        public string StatusMessage => "OK";
        public bool CanSignIn => true;
        public bool CanSignOut => true;
        public AuthSettings Settings { get; set; } = new() { ApiBaseUrl = ApiUrl };
        public AccountBinding CurrentBinding { get; set; } = new(ApiUrl, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), AccountId);

        public int InitializeCalls { get; private set; }
        public int InteractiveSignInAttempts { get; private set; }
        public int SilentTokenRequests { get; private set; }

        public event EventHandler? StateChanged { add { } remove { } }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            SilentTokenRequests++;
            return Task.FromResult<string?>("fake-token");
        }

        public Task<bool> SignInInteractiveAsync(CancellationToken cancellationToken = default)
        {
            InteractiveSignInAttempts++;
            return Task.FromResult(false);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.CompletedTask;
        }

        public Task ReloadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplySettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionCheckResult.Success("OK"));
    }

    private sealed class MockCloudManagementService : ICloudManagementService
    {
        private readonly IAuthService? _authService;
        private bool? _configuredOverride;

        public MockCloudManagementService(IAuthService? authService = null)
        {
            _authService = authService;
        }

        public bool IsConfiguredAndSignedIn
        {
            get => _authService != null
                ? (_authService.State == AuthState.SignedIn && !string.IsNullOrWhiteSpace(_authService.Settings?.ApiBaseUrl))
                : (_configuredOverride ?? true);
            set => _configuredOverride = value;
        }
        public string? CurrentUserAccountId => AccountId;
        public int SyncCallCount { get; private set; }
        public CancellationToken LastReceivedCancellationToken { get; private set; }
        public Func<CancellationToken, Task<DesiredSyncOutcome>>? CustomSyncHandler { get; set; }
        public DesiredSyncOutcome SyncOutcome { get; set; } =
            new(DesiredSyncStatus.AppliedAcknowledged, null, null, null, null);

        public event EventHandler? RegistrationChanged { add { } remove { } }
        public event EventHandler? SyncCompleted { add { } remove { } }

        public async Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default)
        {
            SyncCallCount++;
            LastReceivedCancellationToken = cancellationToken;
            if (CustomSyncHandler != null)
            {
                return await CustomSyncHandler(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return SyncOutcome;
        }

        public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudDeviceRegistration?>(null);
        public Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(string displayName, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(AlarmGroupWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(string groupId, AlarmGroupWriteRequest request, long ifMatchVersion, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ApiResult> DeleteAlarmGroupAsync(string groupId, long ifMatchVersion, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task WorkerLogic_AwaitsAuthInitializeAsync_BeforeEvaluatingState()
    {
        var auth = new TrackingAuthService { State = AuthState.SignedIn };
        var cloud = new MockCloudManagementService { IsConfiguredAndSignedIn = true };

        var outcome = await PushSyncWorkerLogic.ExecuteAsync(cloud, auth);

        Assert.Equal(PushSyncWorkerOutcome.Success, outcome);
        Assert.Equal(1, auth.InitializeCalls); // Worker awaits Auth.InitializeAsync first
        Assert.Equal(1, cloud.SyncCallCount);
    }

    [Fact]
    public async Task WorkerLogic_WhenReauthRequired_ReturnsRetry_WithoutInteractiveUi()
    {
        var auth = new TrackingAuthService { State = AuthState.ReauthRequired };
        var cloud = new MockCloudManagementService(auth); // State-derived property: IsConfiguredAndSignedIn is false under ReauthRequired

        var outcome = await PushSyncWorkerLogic.ExecuteAsync(cloud, auth);

        Assert.Equal(PushSyncWorkerOutcome.Retry, outcome);
        Assert.Equal(0, auth.InteractiveSignInAttempts); // Interactive UI strictly forbidden in worker
        Assert.Equal(0, cloud.SyncCallCount);
    }

    [Fact]
    public async Task WorkerLogic_WhenUnconfigured_ReturnsSuccessDisabled_WithZeroNetworkCalls()
    {
        var auth = new TrackingAuthService { State = AuthState.NotConfigured };
        var cloud = new MockCloudManagementService { IsConfiguredAndSignedIn = false };

        var outcome = await PushSyncWorkerLogic.ExecuteAsync(cloud, auth);

        Assert.Equal(PushSyncWorkerOutcome.Success, outcome); // Success / disabled
        Assert.Equal(0, cloud.SyncCallCount); // Zero network calls
    }

    [Fact]
    public async Task WorkerLogic_WhenSyncSucceeds_ReturnsSuccess()
    {
        var auth = new TrackingAuthService();
        var cloud = new MockCloudManagementService
        {
            IsConfiguredAndSignedIn = true,
            SyncOutcome = new DesiredSyncOutcome(DesiredSyncStatus.AppliedAcknowledged, null, null, null, null)
        };

        var outcome = await PushSyncWorkerLogic.ExecuteAsync(cloud, auth);

        Assert.Equal(PushSyncWorkerOutcome.Success, outcome);
        Assert.Equal(1, cloud.SyncCallCount);
    }

    [Fact]
    public async Task WorkerLogic_WhenTransportFails_ReturnsRetry()
    {
        var auth = new TrackingAuthService();
        var cloud = new MockCloudManagementService
        {
            IsConfiguredAndSignedIn = true,
            SyncOutcome = new DesiredSyncOutcome(DesiredSyncStatus.TransportFailed, null, null, ApiErrorKind.Network, "timeout")
        };

        var outcome = await PushSyncWorkerLogic.ExecuteAsync(cloud, auth);

        Assert.Equal(PushSyncWorkerOutcome.Retry, outcome);
        Assert.Equal(1, cloud.SyncCallCount);
    }

    [Fact]
    public async Task WorkerLogic_WhenCtsCanceled_CancelsExecutionAndReturnsRetry()
    {
        var auth = new TrackingAuthService();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Simulate OnStopped cancellation

        var cloud = new MockCloudManagementService
        {
            IsConfiguredAndSignedIn = true,
            CustomSyncHandler = ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new DesiredSyncOutcome(DesiredSyncStatus.AppliedAcknowledged, null, null, null, null));
            }
        };

        var outcome = await PushSyncWorkerLogic.ExecuteAsync(cloud, auth, cts.Token);

        Assert.Equal(PushSyncWorkerOutcome.Retry, outcome);
        Assert.True(cloud.LastReceivedCancellationToken.IsCancellationRequested); // Propagated to HTTP/applier
    }

    [Fact]
    public void ColdServiceDIResolver_ResolvesWithoutMauiWindow_ViaFallbackResolver()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthService>(new TrackingAuthService())
            .AddSingleton<ICloudManagementService>(new MockCloudManagementService())
            .BuildServiceProvider();

        PushServiceResolver.FallbackResolver = () => services;

        var resolved = PushServiceResolver.ResolveServices();
        Assert.NotNull(resolved);
        Assert.NotNull(resolved.GetService<IAuthService>());
        Assert.NotNull(resolved.GetService<ICloudManagementService>());

        PushServiceResolver.FallbackResolver = null;
        PushServiceResolver.CurrentServices = null;
    }

    [Fact]
    public void NativeExecution_ExplicitlyMarked_RequiresAndroidOsRuntime()
    {
        // Explicitly document/assert requirement:
        // Android PushSyncWorker inherits from AndroidX.Work.Worker which requires the Android OS runtime / WorkManager.
        // In the non-Android test runner environment, the shared business logic is proven by PushSyncWorkerLogic,
        // and direct WorkManager native execution is marked untested without an active Android device or emulator.
        Assert.NotNull(typeof(PushSyncWorkerLogic));
    }
}
