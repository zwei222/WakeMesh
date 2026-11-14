using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

public sealed class PushManagerTokenLifecycleTests
{
    private const string ApiUrl = "https://api.example.com";
    private const string AccountId = "user-123";
    private readonly Guid _deviceId = Guid.NewGuid();

    private sealed class FakeAuthService : IAuthService
    {
        public AuthState State { get; set; } = AuthState.SignedIn;
        public string StatusMessage => "OK";
        public bool CanSignIn => true;
        public bool CanSignOut => true;
        public AuthSettings Settings { get; set; } = new() { ApiBaseUrl = ApiUrl };
        public AccountBinding CurrentBinding { get; set; } = new(ApiUrl, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), AccountId);

        public event EventHandler? StateChanged { add { } remove { } }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake-token");
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReloadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplySettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SignInInteractiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionCheckResult.Success("OK"));
    }

    private sealed class FakeRegistrationStore : ICloudDeviceRegistrationStore
    {
        public CloudDeviceRegistration? Registration { get; set; }

        public Task<CloudDeviceRegistration?> LoadRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Registration);

        public Task SaveRegistrationAsync(CloudDeviceRegistration registration, CancellationToken cancellationToken = default)
        {
            Registration = registration;
            return Task.CompletedTask;
        }

        public Task ClearRegistrationAsync(CancellationToken cancellationToken = default)
        {
            Registration = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePushApiClient : IPushApiClient
    {
        public int PutCalls { get; set; }
        public int DeleteCalls { get; set; }
        public bool SucceedPut { get; set; } = true;
        public bool SucceedDelete { get; set; } = true;

        public bool LastNotificationsCapable { get; set; }

        public Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(Guid deviceId, string pushToken, bool notificationsCapable = false, CancellationToken cancellationToken = default)
        {
            PutCalls++;
            LastNotificationsCapable = notificationsCapable;
            if (SucceedPut)
            {
                return Task.FromResult(ApiResult<PushTokenPutResponse>.Ok(
                    new PushTokenPutResponse(deviceId, DateTimeOffset.UtcNow)));
            }
            return Task.FromResult(ApiResult<PushTokenPutResponse>.Fail(
                ApiErrorKind.Server, 503, "Failed", "Service Unavailable"));
        }

        public Task<ApiResult> DeletePushTokenAsync(Guid deviceId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (SucceedDelete)
            {
                return Task.FromResult(ApiResult.Ok());
            }
            return Task.FromResult(ApiResult.Fail(
                ApiErrorKind.Server, 500, "Failed", "Internal Error"));
        }
    }

    private CloudDeviceRegistration CreateValidRegistration() =>
        new(
            serverDeviceId: _deviceId,
            registrationGeneration: 1,
            apiBaseUrl: ApiUrl,
            accountId: AccountId,
            deviceDisplayName: "Test");

    [Fact]
    public async Task TokenRegistration_WhenSignedInAndRegistered_Succeeds()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = CreateValidRegistration() };
        var tokenStore = new InMemoryPushTokenStore();
        var client = new FakePushApiClient();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

        await manager.OnNewTokenReceivedAsync("token-initial");

        Assert.Equal(PushServiceStatus.Registered, manager.Status);
        Assert.Equal("token-initial", manager.CurrentToken);
        Assert.Equal("token-initial", manager.LastRegisteredToken);
        Assert.Equal(1, client.PutCalls);
    }

    [Fact]
    public async Task TokenRotation_NewTokenReceived_ReRegistersWithServer()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = CreateValidRegistration() };
        var tokenStore = new InMemoryPushTokenStore();
        var client = new FakePushApiClient();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

        await manager.OnNewTokenReceivedAsync("token-1");
        Assert.Equal(PushServiceStatus.Registered, manager.Status);
        Assert.Equal(1, client.PutCalls);

        // Rotation
        await manager.OnNewTokenReceivedAsync("token-2");
        Assert.Equal(PushServiceStatus.Registered, manager.Status);
        Assert.Equal("token-2", manager.CurrentToken);
        Assert.Equal("token-2", manager.LastRegisteredToken);
        Assert.Equal(2, client.PutCalls);
    }

    [Fact]
    public async Task UnregisterToken_CallsDeletePushTokenAsync_AndClearsToken()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = CreateValidRegistration() };
        var tokenStore = new InMemoryPushTokenStore("token-1");
        var client = new FakePushApiClient();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

        var success = await manager.UnregisterTokenAsync();

        Assert.True(success);
        Assert.Equal(PushServiceStatus.Unregistered, manager.Status);
        Assert.Equal(1, client.DeleteCalls);
        Assert.Null(await tokenStore.GetTokenAsync());
    }

    [Fact]
    public async Task UnregisterToken_OnApiFailure_RecordsFailedStateHonestly()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = CreateValidRegistration() };
        var tokenStore = new InMemoryPushTokenStore("token-1");
        var client = new FakePushApiClient { SucceedDelete = false };

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

        var success = await manager.UnregisterTokenAsync();

        Assert.False(success);
        Assert.Equal(PushServiceStatus.UnregistrationFailed, manager.Status);
        Assert.NotNull(manager.LastError);
    }

    [Fact]
    public async Task TokenRegistration_OnApiFailure_RecordsRegistrationFailedHonestly()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = CreateValidRegistration() };
        var tokenStore = new InMemoryPushTokenStore();
        var client = new FakePushApiClient { SucceedPut = false };

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

        await manager.OnNewTokenReceivedAsync("token-test");

        Assert.Equal(PushServiceStatus.RegistrationFailed, manager.Status);
        Assert.NotNull(manager.LastError);
    }

    private sealed class FakeCloudManagementService : ICloudManagementService
    {
        public bool IsConfiguredAndSignedIn => true;
        public string? CurrentUserAccountId => "user-123";
        public event EventHandler? RegistrationChanged;
        public event EventHandler? SyncCompleted { add { } remove { } }

        public void TriggerRegistrationChanged() => RegistrationChanged?.Invoke(this, EventArgs.Empty);

        public Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
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
    public async Task TokenReceived_WhenUnregistered_RemainsPending_UntilCloudRegistrationChanged_ThenPutsToken()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = null };
        var tokenStore = new InMemoryPushTokenStore();
        var client = new FakePushApiClient();
        var cloudService = new FakeCloudManagementService();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client,
            cloudManagementService: cloudService);

        // 1. New token arrives before device registration completes
        await manager.OnNewTokenReceivedAsync("pending-token");

        Assert.Equal("pending-token", manager.CurrentToken);
        Assert.Null(manager.LastRegisteredToken);
        Assert.Equal(0, client.PutCalls); // Not registered yet, PUT deferred

        // 2. Cloud registration completes and fires RegistrationChanged
        regStore.Registration = CreateValidRegistration();
        cloudService.TriggerRegistrationChanged();

        // Allow task to process
        await Task.Delay(50);

        Assert.Equal("pending-token", manager.LastRegisteredToken);
        Assert.Equal(PushServiceStatus.Registered, manager.Status);
        Assert.Equal(1, client.PutCalls);
    }

    [Fact]
    public async Task NotificationPermissionChanged_ReRegisters_WithAccurateNotificationsCapableFlag()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore { Registration = CreateValidRegistration() };
        var tokenStore = new InMemoryPushTokenStore();
        var client = new FakePushApiClient();
        var notifAdapter = new StubPushNotificationAdapter { HasNotificationPermission = false };

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            notifAdapter,
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

        await manager.OnNewTokenReceivedAsync("token-perm-test");
        Assert.Equal(1, client.PutCalls);
        Assert.False(client.LastNotificationsCapable);
        Assert.False(manager.LastSentNotificationsCapable);

        // Now user grants permission
        notifAdapter.HasNotificationPermission = true;

        // Allow event to propagate
        await Task.Delay(50);

        Assert.Equal(2, client.PutCalls);
        Assert.True(client.LastNotificationsCapable);
        Assert.True(manager.LastSentNotificationsCapable);
    }

    [Fact]
    public async Task FilePushTokenStore_SavesDurableToken_AndRetainsAcrossInstances()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "push-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store1 = new FilePushTokenStore(testDir);
            await store1.SaveTokenAsync("secret-fcm-token-12345");

            var store2 = new FilePushTokenStore(testDir);
            var retrieved = await store2.GetTokenAsync();
            Assert.Equal("secret-fcm-token-12345", retrieved);

            await store2.ClearTokenAsync();
            var cleared = await store2.GetTokenAsync();
            Assert.Null(cleared);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }
}
