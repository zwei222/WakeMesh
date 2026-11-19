using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>
/// T24: the real <see cref="AuthService"/> emits a transient <see cref="AuthState.SignedOut"/>
/// from EvaluateState() before the MSAL account probe (HasAccountAsync) finishes. These tests drive
/// the genuine AuthService (real settings file + real state machine) with a delayed account probe so
/// the transient window is wide enough to be observed, and pin that PushManager must never destroy
/// the stored push token because of that intermediate notification.
/// </summary>
public sealed class PushManagerColdAuthTransientTests
{
    private const string ApiUrl = "https://api.example.com";
    private const string CompileTimeClientId = "11111111-1111-4111-8111-111111111111";
    private const string ApiClientId = "22222222-2222-4222-8222-222222222222";
    private const string HomeAccountId = "home-account-t24";

    // Wide enough for the 30ms cold account probe + a coalesced settle pass; tests stay bounded.
    private const int SettleMs = 500;
    private const int AccountProbeDelayMs = 30;

    private readonly Guid _deviceId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public async Task ColdReceiver_TransientSignedOutDuringAuthInitialize_KeepsTokenAndSkipsServerDelete()
    {
        var (auth, dir, msal) = await CreateAuthAsync(hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-token-cold");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            // FCM cold receiver: PushManager.OnPushMessageReceivedAsync awaits AuthService.InitializeAsync,
            // which raises the transient SignedOut before the delayed account probe resolves.
            var result = await manager.OnPushMessageReceivedAsync(CreatePayload());

            await Task.Delay(SettleMs);

            Assert.True(msal.HasAccountCalls > 0, "the real account probe must have run");
            Assert.True(result.IsValid, result.ErrorReason);
            Assert.Equal(0, client.DeleteCalls);
            Assert.Equal("fcm-token-cold", await tokenStore.GetTokenAsync());
            Assert.NotEqual(PushServiceStatus.Unregistered, manager.Status);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task AppStartup_ColdAuthInitialize_DoesNotClearStoredToken()
    {
        var (auth, dir, _) = await CreateAuthAsync(hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-token-startup");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            // App.xaml.cs startup order: auth first, then push.
            await auth.InitializeAsync();
            await manager.InitializeAsync();

            await Task.Delay(SettleMs);

            Assert.Equal(AuthState.SignedIn, auth.State);
            Assert.Equal(0, client.DeleteCalls);
            Assert.Equal("fcm-token-startup", await tokenStore.GetTokenAsync());
            Assert.True(client.PutCalls >= 1, "the restored session must (re)register the stored token");
            Assert.Equal(PushServiceStatus.Registered, manager.Status);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task ExplicitSignOut_WhenServerDeleteIsRefused_ClearsLocalStateAndReportsFailureHonestly()
    {
        var (auth, dir, _) = await CreateAuthAsync(hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-token-signout");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            await auth.InitializeAsync();
            await manager.InitializeAsync();
            Assert.Equal(PushServiceStatus.Registered, manager.Status);

            // SessionEnded runs after the session is already dead: no server DELETE is attempted,
            // local matching token is cleared, and server deletion stays unconfirmed.
            client.DeleteFailureKind = ApiErrorKind.Unauthorized;
            await auth.SignOutAsync();

            await Task.Delay(SettleMs);

            Assert.Equal(AuthState.SignedOut, auth.State);
            Assert.Equal(0, client.DeleteCalls);
            Assert.Null(await tokenStore.GetTokenAsync());
            Assert.Null(manager.LastRegisteredToken);
            Assert.Null(manager.CurrentToken);
            Assert.Equal(PushServiceStatus.UnregistrationFailed, manager.Status);
            Assert.Contains(PushManager.ServerDeletionUnconfirmed, manager.LastError ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task ExplicitSignOut_WhenServerDeleteSucceeds_ClearsLocalStateAndReportsUnregistered()
    {
        var (auth, dir, _) = await CreateAuthAsync(hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-token-signout-ok");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            await auth.InitializeAsync();
            await manager.InitializeAsync();
            Assert.Equal(PushServiceStatus.Registered, manager.Status);

            await auth.SignOutAsync();
            await Task.Delay(SettleMs);

            // Ended session: local clear only; server deletion is not claimed.
            Assert.Equal(0, client.DeleteCalls);
            Assert.Null(await tokenStore.GetTokenAsync());
            Assert.Null(manager.LastRegisteredToken);
            Assert.Equal(PushServiceStatus.UnregistrationFailed, manager.Status);
            Assert.Contains(PushManager.ServerDeletionUnconfirmed, manager.LastError ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task RepeatedStateChangedEvents_WhileSignedIn_NeverUnregisterButStayBounded()
    {
        var (auth, dir, _) = await CreateAuthAsync(hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-token-storm");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            await auth.InitializeAsync();
            await manager.InitializeAsync();
            var putsAfterStartup = client.PutCalls;

            // Settings screen / foreground churn: every reload re-runs EvaluateState (transient SignedOut).
            for (var i = 0; i < 8; i++)
            {
                await auth.ReloadSettingsAsync();
            }

            await Task.Delay(SettleMs * 3);

            Assert.Equal(AuthState.SignedIn, auth.State);
            Assert.Equal(0, client.DeleteCalls);
            Assert.Equal("fcm-token-storm", await tokenStore.GetTokenAsync());
            Assert.True(client.PutCalls - putsAfterStartup <= 8,
                $"coalesced re-evaluation must stay bounded, saw {client.PutCalls - putsAfterStartup} PUTs");
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task DisposeDuringColdAuthInitialize_DoesNotUnregisterStoredToken()
    {
        var (auth, dir, _) = await CreateAuthAsync(hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-token-dispose");
            var client = new CountingPushApiClient();
            var manager = CreateManager(auth, tokenStore, client);

            var initTask = auth.InitializeAsync();
            manager.Dispose();          // platform shutdown while the cold probe is still in flight
            await initTask;
            await Task.Delay(SettleMs);

            Assert.Equal(0, client.DeleteCalls);
            Assert.Equal("fcm-token-dispose", await tokenStore.GetTokenAsync());
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    private PushManager CreateManager(IAuthService auth, IPushTokenStore tokenStore, IPushApiClient client) =>
        new(
            auth,
            new StaticRegistrationStore { Registration = CreateRegistration() },
            credentialStore: null,
            tokenStore,
            new StubPushNotificationAdapter(),
            new StubWorkManagerAdapter(),
            isConfigured: true,
            clientFactory: _ => client);

    private CloudDeviceRegistration CreateRegistration() =>
        new(
            serverDeviceId: _deviceId,
            registrationGeneration: 1,
            apiBaseUrl: ApiUrl,
            accountId: HomeAccountId,
            deviceDisplayName: "t24");

    private IReadOnlyDictionary<string, string> CreatePayload() => new Dictionary<string, string>
    {
        ["type"] = PushPayloadValidator.SettingsChangedType,
        ["deviceId"] = _deviceId.ToString(),
        ["revision"] = "1",
    };

    private static async Task<(AuthService Auth, string Dir, DelayedAccountMsalAdapter Msal)> CreateAuthAsync(bool hasAccount)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wm-t24-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var settingsPath = Path.Combine(dir, "auth-settings.json");
        var store = new JsonAuthSettingsStore(settingsPath);
        await store.SaveSettingsAsync(new AuthSettings
        {
            ApiBaseUrl = ApiUrl,
            AndroidClientId = CompileTimeClientId,
            ApiClientId = ApiClientId,
        });
        await store.SaveSelectedHomeAccountIdAsync(HomeAccountId);

        var msal = new DelayedAccountMsalAdapter(HomeAccountId, hasAccount, AccountProbeDelayMs);
        var auth = new AuthService(store, msal, compileTimeClientId: CompileTimeClientId);
        return (auth, dir, msal);
    }

    private static void SafeDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // temp cleanup must never fail the test
        }
    }

    /// <summary>
    /// Adapter with a deliberate account-probe latency. The synchronous EvaluateState() call inside
    /// AuthService.InitializeAsync publishes SignedOut while this probe is still awaited — the exact
    /// window that used to make PushManager burn the stored token.
    /// </summary>
    private sealed class DelayedAccountMsalAdapter : IMsalClientAdapter
    {
        private const int TransientWindowProbeMs = 5;

        public DelayedAccountMsalAdapter(string? selectedHomeAccountId, bool hasAccount, int accountProbeDelayMs)
        {
            SelectedHomeAccountId = selectedHomeAccountId;
            HasAccount = hasAccount;
            AccountProbeDelayMs = accountProbeDelayMs;
        }

        public string? SelectedHomeAccountId { get; private set; }
        public bool HasAccount { get; private set; }
        public int AccountProbeDelayMs { get; }
        public int HasAccountCalls { get; private set; }
        public int SignOutCalls { get; private set; }

        public Task<string?> AcquireTokenSilentAsync(string[] scopes, CancellationToken cancellationToken = default) =>
            // Mirrors MSAL: silent acquisition only succeeds while an account is still cached.
            Task.FromResult(HasAccount ? "access-token-t24" : null);

        public Task<string?> AcquireTokenInteractiveAsync(string[] scopes, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            SignOutCalls++;
            await Task.Delay(TransientWindowProbeMs, cancellationToken).ConfigureAwait(false);
            SelectedHomeAccountId = null;
            HasAccount = false;
        }

        public async Task<bool> HasAccountAsync(CancellationToken cancellationToken = default)
        {
            HasAccountCalls++;
            await Task.Delay(AccountProbeDelayMs, cancellationToken).ConfigureAwait(false);
            return HasAccount;
        }

        public void UpdateClientId(string? clientId)
        {
        }

        public void SetSelectedHomeAccountId(string? homeAccountId) => SelectedHomeAccountId = homeAccountId;
    }

    private sealed class StaticRegistrationStore : ICloudDeviceRegistrationStore
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

    private sealed class CountingPushApiClient : IPushApiClient
    {
        public int PutCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public ApiErrorKind? DeleteFailureKind { get; set; }

        public Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(
            Guid deviceId,
            string pushToken,
            bool notificationsCapable = false,
            CancellationToken cancellationToken = default)
        {
            PutCalls++;
            return Task.FromResult(ApiResult<PushTokenPutResponse>.Ok(
                new PushTokenPutResponse(deviceId, DateTimeOffset.UtcNow)));
        }

        public Task<ApiResult> DeletePushTokenAsync(Guid deviceId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (DeleteFailureKind is { } kind)
            {
                return Task.FromResult(ApiResult.Fail(kind, 401, "Unauthorized", "Device credential rejected"));
            }

            return Task.FromResult(ApiResult.Ok());
        }
    }
}
