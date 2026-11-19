using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

public sealed class PushManagerEnqueueAndNotificationTests
{
    private const string ApiUrl = "https://api.example.com";
    private const string AccountId = "user-123";
    private readonly Guid _deviceId = Guid.NewGuid();

    private sealed class FakeAuthService : IAuthService
    {
        public bool RestoresFromColdSettings { get; set; }
        public int InitializeCalls { get; private set; }

        public AuthState State { get; set; } = AuthState.SignedIn;
        public string StatusMessage => "OK";
        public bool CanSignIn => true;
        public bool CanSignOut => true;
        public AuthSettings Settings { get; set; } = new() { ApiBaseUrl = ApiUrl };
        public AccountBinding CurrentBinding { get; set; } = new(ApiUrl, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), AccountId);

#pragma warning disable CS0067
        public event EventHandler? StateChanged;
#pragma warning restore CS0067

        public Task<string?> GetAccessTokenAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake-token");
        public Task InitializeAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            if (RestoresFromColdSettings)
            {
                Settings = new AuthSettings { ApiBaseUrl = ApiUrl };
                CurrentBinding = new AccountBinding(ApiUrl, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), AccountId);
            }
            return Task.CompletedTask;
        }
        public Task ReloadSettingsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplySettingsAsync(AuthSettings settings, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSettingsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SignInInteractiveAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SignOutAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ConnectionCheckResult> CheckConnectionAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionCheckResult.Success("OK"));

        public void TriggerStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeRegistrationStore : ICloudDeviceRegistrationStore
    {
        public CloudDeviceRegistration? Registration { get; set; }

        public Task<CloudDeviceRegistration?> LoadRegistrationAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(Registration);

        public Task SaveRegistrationAsync(CloudDeviceRegistration registration, System.Threading.CancellationToken cancellationToken = default)
        {
            Registration = registration;
            return Task.CompletedTask;
        }

        public Task ClearRegistrationAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            Registration = null;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OnPushMessageReceived_ValidPayload_EnqueuesWorkAndShowsNotification()
    {
        var auth = new FakeAuthService
        {
            RestoresFromColdSettings = true,
            Settings = new AuthSettings(), // Cold state: uninitialized
            CurrentBinding = null! // Cold state: uninitialized
        };
        var regStore = new FakeRegistrationStore
        {
            Registration = new CloudDeviceRegistration(
                serverDeviceId: _deviceId,
                registrationGeneration: 1,
                apiBaseUrl: ApiUrl,
                accountId: AccountId,
                deviceDisplayName: "Test")
        };
        var tokenStore = new InMemoryPushTokenStore();
        var notifAdapter = new StubPushNotificationAdapter { HasNotificationPermission = true };
        var workAdapter = new StubWorkManagerAdapter();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            notifAdapter,
            workAdapter,
            isConfigured: true);

        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _deviceId.ToString("D"),
            ["revision"] = "5"
        };

        var result = await manager.OnPushMessageReceivedAsync(data);

        Assert.True(result.IsValid);
        Assert.Equal(1, auth.InitializeCalls);
        Assert.Equal(1, notifAdapter.NotificationShownCount);
        Assert.Equal(1, workAdapter.EnqueueCount);
    }

    [Fact]
    public async Task OnPushMessageReceived_WithoutNotificationPermission_EnqueuesWorkWithoutNotification()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore
        {
            Registration = new CloudDeviceRegistration(
                serverDeviceId: _deviceId,
                registrationGeneration: 1,
                apiBaseUrl: ApiUrl,
                accountId: AccountId,
                deviceDisplayName: "Test")
        };
        var tokenStore = new InMemoryPushTokenStore();
        var notifAdapter = new StubPushNotificationAdapter { HasNotificationPermission = false };
        var workAdapter = new StubWorkManagerAdapter();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            notifAdapter,
            workAdapter,
            isConfigured: true);

        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _deviceId.ToString("D"),
            ["revision"] = "5"
        };

        var result = await manager.OnPushMessageReceivedAsync(data);

        Assert.True(result.IsValid);
        Assert.Equal(0, notifAdapter.NotificationShownCount);
        Assert.Equal(1, workAdapter.EnqueueCount);
    }

    [Fact]
    public async Task OnPushMessageReceived_MismatchedDevice_RejectsAndDoesNotEnqueueOrNotify()
    {
        var auth = new FakeAuthService();
        var regStore = new FakeRegistrationStore
        {
            Registration = new CloudDeviceRegistration(
                serverDeviceId: _deviceId,
                registrationGeneration: 1,
                apiBaseUrl: ApiUrl,
                accountId: AccountId,
                deviceDisplayName: "Test")
        };
        var tokenStore = new InMemoryPushTokenStore();
        var notifAdapter = new StubPushNotificationAdapter();
        var workAdapter = new StubWorkManagerAdapter();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            notifAdapter,
            workAdapter,
            isConfigured: true);

        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = Guid.NewGuid().ToString("D"), // different device
            ["revision"] = "5"
        };

        var result = await manager.OnPushMessageReceivedAsync(data);

        Assert.False(result.IsValid);
        Assert.Equal(PushValidationStatus.DeviceMismatch, result.Status);
        Assert.Equal(0, notifAdapter.NotificationShownCount);
        Assert.Equal(0, workAdapter.EnqueueCount);
    }
}
