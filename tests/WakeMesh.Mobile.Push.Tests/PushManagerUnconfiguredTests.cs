using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

public sealed class PushManagerUnconfiguredTests
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

    private sealed class FakePushApiClient : IPushApiClient
    {
        public int CallCount { get; private set; }

        public Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(Guid deviceId, string pushToken, bool notificationsCapable = false, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ApiResult<PushTokenPutResponse>.Ok(
                new PushTokenPutResponse(deviceId, DateTimeOffset.UtcNow)));
        }

        public Task<ApiResult> DeletePushTokenAsync(Guid deviceId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ApiResult.Ok());
        }
    }

    [Fact]
    public async Task Unconfigured_StaysNotConfigured_DoesNotMakeNetworkCalls_DoesNotDisguiseSuccess()
    {
        var auth = new FakeAuthService();
        var regStore = new FileCloudDeviceRegistrationStore();
        var tokenStore = new InMemoryPushTokenStore();
        var notifAdapter = new StubPushNotificationAdapter();
        var workAdapter = new StubWorkManagerAdapter();
        var client = new FakePushApiClient();

        using var manager = new PushManager(
            auth,
            regStore,
            credentialStore: null,
            tokenStore,
            notifAdapter,
            workAdapter,
            isConfigured: false,
            clientFactory: _ => client);

        Assert.Equal(PushServiceStatus.NotConfigured, manager.Status);
        Assert.False(manager.IsConfigured);

        await manager.InitializeAsync();
        Assert.Equal(PushServiceStatus.NotConfigured, manager.Status);
        Assert.Equal(0, client.CallCount);

        await manager.OnNewTokenReceivedAsync("some-token");
        Assert.Equal(PushServiceStatus.NotConfigured, manager.Status);
        Assert.Equal(0, client.CallCount);

        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _deviceId.ToString("D"),
            ["revision"] = "1"
        };
        var pushResult = await manager.OnPushMessageReceivedAsync(data);
        Assert.False(pushResult.IsValid);
        Assert.Equal(0, notifAdapter.NotificationShownCount);
        Assert.Equal(0, workAdapter.EnqueueCount);
        Assert.Equal(0, client.CallCount);
    }
}
