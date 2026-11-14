using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

/// <summary>
/// T26: destructive push cleanup is driven only by explicit <see cref="IAuthSessionLifecycle.SessionEnded"/>,
/// never by StateChanged speculation, GetAccessToken probes, or settle-window heuristics.
/// </summary>
public sealed class PushManagerExplicitAuthLifecycleTests
{
    private const string ApiUrl = "https://api.example.com";
    private const string CompileTimeClientId = "11111111-1111-4111-8111-111111111111";
    private const string ApiClientId = "22222222-2222-4222-8222-222222222222";
    private const string HomeAccountId = "home-account-t26";
    private readonly Guid _deviceId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(400)]
    public async Task ColdReceiver_WithRealisticMsalUiRequired_KeepsToken_ForAllAccountProbeDelays(int hasDelayMs)
    {
        var (auth, dir, msal) = await CreateAuthAsync(MsalMode.RealisticUiRequired, hasDelayMs, hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-cold-t26");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            var result = await manager.OnPushMessageReceivedAsync(CreatePayload());
            await WaitUntilAsync(() => auth.IsInitialized && auth.State == AuthState.SignedIn, hasDelayMs + 2000);

            Assert.True(result.IsValid, result.ErrorReason);
            Assert.Equal(0, client.DeleteCalls);
            Assert.Equal("fcm-cold-t26", await tokenStore.GetTokenAsync());
            Assert.NotEqual(PushServiceStatus.Unregistered, manager.Status);
            Assert.NotEqual(PushServiceStatus.UnregistrationFailed, manager.Status);
            Assert.Equal(0, msal.SilentCalls);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task ExplicitSignOut_WithMsalUiRequiredOnSilent_StaysSignedOut_ClearsMatchingLocalToken_ReportsServerDeletionUnconfirmed()
    {
        var (auth, dir, msal) = await CreateAuthAsync(MsalMode.RealisticUiRequired, hasDelayMs: 30, hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-signout-t26");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            await auth.InitializeAsync();
            await manager.InitializeAsync();
            await WaitUntilAsync(() => manager.Status == PushServiceStatus.Registered, 2000);
            Assert.Equal(PushServiceStatus.Registered, manager.Status);

            // Stamp binding the way a durable store would after registration.
            Assert.False(string.IsNullOrEmpty(await tokenStore.GetBindingKeyAsync()));

            client.DeleteFailureKind = ApiErrorKind.Unauthorized;
            var silentBefore = msal.SilentCalls;
            await auth.SignOutAsync();

            await WaitUntilAsync(() => manager.Status is PushServiceStatus.UnregistrationFailed or PushServiceStatus.Unregistered, 2000);

            Assert.Equal(AuthState.SignedOut, auth.State);
            Assert.Equal(0, client.DeleteCalls); // ended session must not pretend a server DELETE ran
            Assert.Equal(silentBefore, msal.SilentCalls); // no token-probe after sign-out
            Assert.Null(await tokenStore.GetTokenAsync());
            Assert.Null(manager.CurrentToken);
            Assert.Null(manager.LastRegisteredToken);
            Assert.Equal(PushServiceStatus.UnregistrationFailed, manager.Status);
            Assert.Contains("ServerDeletionUnconfirmed", manager.LastError ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task ExplicitSignOut_PublicUnregisterWhileSignedIn_ServerRefuse_KeepsLocalToken()
    {
        var (auth, dir, _) = await CreateAuthAsync(MsalMode.RealisticUiRequired, hasDelayMs: 0, hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-unregister-refuse");
            var client = new CountingPushApiClient { DeleteFailureKind = ApiErrorKind.Unauthorized };
            using var manager = CreateManager(auth, tokenStore, client);

            await auth.InitializeAsync();
            await manager.InitializeAsync();
            await WaitUntilAsync(() => manager.Status == PushServiceStatus.Registered, 2000);

            var ok = await manager.UnregisterTokenAsync();

            Assert.False(ok);
            Assert.Equal(1, client.DeleteCalls);
            Assert.Equal("fcm-unregister-refuse", await tokenStore.GetTokenAsync());
            Assert.Equal(PushServiceStatus.UnregistrationFailed, manager.Status);
            Assert.Equal(AuthState.SignedIn, auth.State);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task ApplySettings_IdentityChanged_BothScopeFields_ClearsOnlyOldBindingToken()
    {
        var (auth, dir, _) = await CreateAuthAsync(MsalMode.RealisticUiRequired, hasDelayMs: 0, hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore();
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            await auth.InitializeAsync();
            await manager.OnNewTokenReceivedAsync("token-old-scope");
            await WaitUntilAsync(() => manager.Status == PushServiceStatus.Registered, 2000);
            var oldKey = await tokenStore.GetBindingKeyAsync();
            Assert.False(string.IsNullOrEmpty(oldKey));

            var newApiClient = "44444444-4444-4444-8444-444444444444";
            await auth.ApplySettingsAsync(new AuthSettings
            {
                ApiBaseUrl = "https://other.example.com",
                AndroidClientId = CompileTimeClientId,
                ApiClientId = newApiClient,
            });

            await WaitUntilAsync(() => manager.Status is PushServiceStatus.UnregistrationFailed or PushServiceStatus.Unregistered, 2000);

            Assert.Null(await tokenStore.GetTokenAsync());
            Assert.Null(manager.CurrentToken);
            Assert.Equal(0, client.DeleteCalls);
            Assert.Contains("ServerDeletionUnconfirmed", manager.LastError ?? string.Empty, StringComparison.Ordinal);

            // New scope registration must not be wiped by a late SessionEnded for the old binding.
            auth.GetType(); // keep real auth
            // Re-sign is not automatic after identity change; inject a lifecycle-capable signed-in fake path
            // by receiving a token after a fresh interactive sign-in is not required here: stamp via manager
            // after manually restoring a session is covered by the race test below.
            _ = oldKey;
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task SessionEndedRace_NewScopeToken_IsNotClearedByOldBindingEvent()
    {
        var tokenStore = new InMemoryPushTokenStore();
        var client = new CountingPushApiClient();
        var auth = new LifecycleAuthFake
        {
            State = AuthState.SignedIn,
            Settings = new AuthSettings { ApiBaseUrl = ApiUrl, AndroidClientId = CompileTimeClientId, ApiClientId = ApiClientId },
            CurrentBinding = new AccountBinding(ApiUrl, ApiClientId, CompileTimeClientId, HomeAccountId),
        };

        using var manager = CreateManager(auth, tokenStore, client);
        await manager.OnNewTokenReceivedAsync("token-scope-a");
        Assert.Equal(PushServiceStatus.Registered, manager.Status);
        var oldBinding = auth.CurrentBinding;

        // Switch to a new scope and register a replacement token before the old SessionEnded is delivered.
        auth.CurrentBinding = new AccountBinding("https://other.example.com", ApiClientId, CompileTimeClientId, "home-b");
        auth.Settings = new AuthSettings
        {
            ApiBaseUrl = "https://other.example.com",
            AndroidClientId = CompileTimeClientId,
            ApiClientId = ApiClientId,
        };
        await manager.OnNewTokenReceivedAsync("token-scope-b");
        Assert.Equal("token-scope-b", manager.CurrentToken);

        auth.RaiseSessionEnded(oldBinding, AuthSessionEndReason.IdentityChanged);
        await WaitUntilAsync(async () =>
            (await tokenStore.GetTokenAsync()) == "token-scope-b", 1000);

        Assert.Equal("token-scope-b", await tokenStore.GetTokenAsync());
        Assert.Equal("token-scope-b", manager.CurrentToken);
        Assert.Equal(0, client.DeleteCalls);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(200)]
    public async Task OfflineHasAccount_SlowReloadStorm_NeverDeletesToken(int hasDelayMs)
    {
        var (auth, dir, msal) = await CreateAuthAsync(MsalMode.OfflineNetwork, hasDelayMs, hasAccount: true);
        try
        {
            var tokenStore = new InMemoryPushTokenStore("fcm-storm-t26");
            var client = new CountingPushApiClient();
            using var manager = CreateManager(auth, tokenStore, client);

            // Start signed-in via realistic account presence, then flip MSAL to offline failures.
            msal.Mode = MsalMode.RealisticUiRequired;
            await auth.InitializeAsync();
            await manager.InitializeAsync();
            await WaitUntilAsync(() => auth.State == AuthState.SignedIn, hasDelayMs + 2000);
            msal.Mode = MsalMode.OfflineNetwork;

            var puts = client.PutCalls;
            for (var i = 0; i < 8; i++)
            {
                await auth.ReloadSettingsAsync();
            }

            await WaitUntilAsync(() => auth.State is AuthState.SignedIn or AuthState.ReauthRequired, hasDelayMs + 3000);

            Assert.Equal(0, client.DeleteCalls);
            Assert.Equal("fcm-storm-t26", await tokenStore.GetTokenAsync());
            Assert.True(client.PutCalls - puts <= 8);
            Assert.NotEqual(PushServiceStatus.Unregistered, manager.Status);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task AuthService_GetAccessToken_AfterExplicitSignOut_WithMsalUiRequired_DoesNotRewriteSignedOut()
    {
        var (auth, dir, msal) = await CreateAuthAsync(MsalMode.RealisticUiRequired, hasDelayMs: 0, hasAccount: true);
        try
        {
            await auth.InitializeAsync();
            Assert.Equal(AuthState.SignedIn, auth.State);

            await auth.SignOutAsync();
            Assert.Equal(AuthState.SignedOut, auth.State);

            msal.ForceUiRequiredOnSilent = true;
            var token = await auth.GetAccessTokenAsync();

            Assert.Null(token);
            Assert.Equal(AuthState.SignedOut, auth.State);
        }
        finally
        {
            SafeDelete(dir);
        }
    }

    [Fact]
    public async Task FilePushTokenStore_PersistsBindingMetadata_WithToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "t26-token-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            var store1 = new FilePushTokenStore(path);
            await store1.SaveTokenWithBindingAsync("tok-meta", "binding-abc");
            var store2 = new FilePushTokenStore(path);
            Assert.Equal("tok-meta", await store2.GetTokenAsync());
            Assert.Equal("binding-abc", await store2.GetBindingKeyAsync());
            await store2.ClearTokenAsync();
            Assert.Null(await store2.GetTokenAsync());
            Assert.Null(await store2.GetBindingKeyAsync());
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            try { if (File.Exists(path + ".binding")) File.Delete(path + ".binding"); } catch { /* ignore */ }
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
            deviceDisplayName: "t26");

    private IReadOnlyDictionary<string, string> CreatePayload() => new Dictionary<string, string>
    {
        ["type"] = PushPayloadValidator.SettingsChangedType,
        ["deviceId"] = _deviceId.ToString(),
        ["revision"] = "1",
    };

    private static async Task<(AuthService Auth, string Dir, RealisticMsalAdapter Msal)> CreateAuthAsync(
        MsalMode mode,
        int hasDelayMs,
        bool hasAccount)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wm-t26-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var store = new JsonAuthSettingsStore(Path.Combine(dir, "auth-settings.json"));
        await store.SaveSettingsAsync(new AuthSettings
        {
            ApiBaseUrl = ApiUrl,
            AndroidClientId = CompileTimeClientId,
            ApiClientId = ApiClientId,
        });
        await store.SaveSelectedHomeAccountIdAsync(HomeAccountId);

        var msal = new RealisticMsalAdapter(HomeAccountId, hasAccount, hasDelayMs, mode);
        var auth = new AuthService(store, msal, compileTimeClientId: CompileTimeClientId);
        return (auth, dir, msal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }
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

    private enum MsalMode
    {
        RealisticUiRequired,
        OfflineNetwork,
    }

    /// <summary>
    /// Mirrors MsalClientAdapter: missing/cleared account → <see cref="MsalUiRequiredException"/> (not null).
    /// </summary>
    private sealed class RealisticMsalAdapter : IMsalClientAdapter
    {
        public RealisticMsalAdapter(string? selectedHomeAccountId, bool hasAccount, int hasDelayMs, MsalMode mode)
        {
            SelectedHomeAccountId = selectedHomeAccountId;
            HasAccount = hasAccount;
            HasDelayMs = hasDelayMs;
            Mode = mode;
        }

        public string? SelectedHomeAccountId { get; private set; }
        public bool HasAccount { get; private set; }
        public int HasDelayMs { get; }
        public MsalMode Mode { get; set; }
        public bool ForceUiRequiredOnSilent { get; set; }
        public int SilentCalls { get; private set; }
        public int InteractiveCalls { get; private set; }

        public Task<string?> AcquireTokenSilentAsync(string[] scopes, CancellationToken cancellationToken = default)
        {
            SilentCalls++;
            if (ForceUiRequiredOnSilent || Mode == MsalMode.RealisticUiRequired)
            {
                if (string.IsNullOrEmpty(SelectedHomeAccountId) || !HasAccount)
                {
                    throw new MsalUiRequiredException("user_null", "no account");
                }
            }

            if (Mode == MsalMode.OfflineNetwork)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult<string?>("access-token-t26");
        }

        public Task<string?> AcquireTokenInteractiveAsync(string[] scopes, CancellationToken cancellationToken = default)
        {
            InteractiveCalls++;
            return Task.FromResult<string?>(null);
        }

        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            SelectedHomeAccountId = null;
            HasAccount = false;
        }

        public async Task<bool> HasAccountAsync(CancellationToken cancellationToken = default)
        {
            if (HasDelayMs > 0)
            {
                await Task.Delay(HasDelayMs, cancellationToken).ConfigureAwait(false);
            }

            if (Mode == MsalMode.OfflineNetwork)
            {
                throw new HttpRequestException("offline");
            }

            return HasAccount && !string.IsNullOrEmpty(SelectedHomeAccountId);
        }

        public void UpdateClientId(string? clientId)
        {
        }

        public void SetSelectedHomeAccountId(string? homeAccountId) => SelectedHomeAccountId = homeAccountId;
    }

    private sealed class LifecycleAuthFake : IAuthService, IAuthSessionLifecycle
    {
        public AuthState State { get; set; } = AuthState.SignedIn;
        public string StatusMessage => "OK";
        public bool CanSignIn => true;
        public bool CanSignOut => true;
        public AuthSettings Settings { get; set; } = new();
        public AccountBinding CurrentBinding { get; set; } = new();
        public event EventHandler? StateChanged;
        public event EventHandler<AuthSessionEndedEventArgs>? SessionEnded;

        public void RaiseSessionEnded(AccountBinding oldBinding, AuthSessionEndReason reason) =>
            SessionEnded?.Invoke(this, new AuthSessionEndedEventArgs(oldBinding, reason));

        public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake");
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReloadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplySettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SignInInteractiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionCheckResult.Success("OK"));
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
