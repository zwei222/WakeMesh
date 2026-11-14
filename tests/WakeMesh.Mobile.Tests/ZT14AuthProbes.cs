using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using WakeMesh.Client;
using WakeMesh.Mobile;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// Permanent T14 regression probes: shared DI, delayed settings load, process recreation,
/// binding-scoped credential isolation, and honest MSAL account restore.
/// </summary>
public sealed class T14Adapter : IMsalClientAdapter
{
    public string? NextAccount = "A";
    public string? Selected;
    public int Silent, Interactive, SignOuts;
    public bool CacheHasSelected = true;
    public System.Collections.Generic.List<string> Scopes { get; } = new();
    public string? SelectedHomeAccountId => Selected;

    public void UpdateClientId(string? c) { }

    public void SetSelectedHomeAccountId(string? homeAccountId)
    {
        Selected = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId;
    }

    public Task<string?> AcquireTokenSilentAsync(string[] s, CancellationToken c = default)
    {
        Silent++;
        Scopes.AddRange(s);
        if (Selected == null || !CacheHasSelected)
        {
            throw new MsalUiRequiredException("user_null", "x");
        }

        return Task.FromResult<string?>("tok-" + Selected);
    }

    public Task<string?> AcquireTokenInteractiveAsync(string[] s, CancellationToken c = default)
    {
        Interactive++;
        Scopes.AddRange(s);
        Selected = NextAccount;
        CacheHasSelected = true;
        return Task.FromResult<string?>("tok-" + Selected);
    }

    public Task SignOutAsync(CancellationToken c = default)
    {
        SignOuts++;
        Selected = null;
        CacheHasSelected = false;
        return Task.CompletedTask;
    }

    public Task<bool> HasAccountAsync(CancellationToken c = default) =>
        Task.FromResult(Selected != null && CacheHasSelected);
}

public sealed class T14FixedDevice : IDeviceInfoProvider
{
    public string DeviceId { get; } = Guid.NewGuid().ToString("D");
    public string DeviceDisplayName => "t14";
}

public sealed class T14CountingStorage : ISecureStorageProvider
{
    public readonly ConcurrentDictionary<string, string> D = new();
    public Task<string?> GetAsync(string k)
    {
        D.TryGetValue(k, out var v);
        return Task.FromResult<string?>(v);
    }

    public Task SetAsync(string k, string v)
    {
        D[k] = v;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string k) => Task.FromResult(D.TryRemove(k, out _));
    public void RemoveAll() => D.Clear();
}

/// <summary>
/// Completes LoadSettingsAsync only after a delay — exercises the async gate (no empty substitute).
/// </summary>
public sealed class T14DelayedSettingsStore : IAuthSettingsStore
{
    private readonly JsonAuthSettingsStore _inner;
    private readonly int _delayMs;
    public int LoadCalls;

    public T14DelayedSettingsStore(string path, int delayMs = 150)
    {
        _inner = new JsonAuthSettingsStore(path);
        _delayMs = delayMs;
    }

    public async Task<AuthSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        LoadCalls++;
        await Task.Delay(_delayMs, cancellationToken);
        return await _inner.LoadSettingsAsync(cancellationToken);
    }

    public Task SaveSettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default) =>
        _inner.SaveSettingsAsync(settings, cancellationToken);

    public JsonAuthSettingsStore Inner => _inner;
}

public sealed class ZT14AuthProbes
{
    private readonly ITestOutputHelper _o;
    public ZT14AuthProbes(ITestOutputHelper o) => _o = o;

    const string Api = "22222222-2222-2222-2222-222222222222";
    /// <summary>Fictional placeholder zero GUID — used only via injected compileTimeClientId, never as test project default.</summary>
    const string ZeroClientId = "00000000-0000-0000-0000-000000000000";

    static string Tmp(string p) => Path.Combine(Path.GetTempPath(), p + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Assert test assembly CompiledAuthConstants is the managed-test fictional nonzero default
    /// (no -p:WakeMeshAuthClientId required). App body may still default to zero.
    /// </summary>
    static void AssertTestCompileTimeClientIdIsNonZeroFictional()
    {
        Assert.NotEqual(ZeroClientId, CompiledAuthConstants.CompileTimeClientId);
        Assert.False(string.IsNullOrWhiteSpace(CompiledAuthConstants.CompileTimeClientId));
        Assert.True(
            Guid.TryParseExact(CompiledAuthConstants.CompileTimeClientId, "d", out _),
            "CompileTimeClientId must be canonical lowercase 'd' GUID");
        Assert.Equal(
            CompiledAuthConstants.CompileTimeClientId,
            CompiledAuthConstants.CompileTimeClientId.ToLowerInvariant());
        Assert.StartsWith("msal" + CompiledAuthConstants.CompileTimeClientId, CompiledAuthConstants.CompileTimeScheme);
        Assert.Equal(
            "msal" + CompiledAuthConstants.CompileTimeClientId + "://auth",
            CompiledAuthConstants.CompileTimeRedirectUri);
    }

    static ServiceProvider Boot(
        string settingsPath,
        IMsalClientAdapter adapter,
        ISecureStorageProvider sec,
        IAuthSettingsStore? settingsStore = null,
        IDeviceInfoProvider? device = null,
        string? compileTimeClientId = null)
    {
        var sc = new ServiceCollection();
        sc.AddWakeMeshCoreServices(Tmp("a") + ".json", Tmp("d") + ".txt");
        if (settingsStore != null)
        {
            sc.AddSingleton<IAuthSettingsStore>(settingsStore);
            sc.AddSingleton<IDeviceCredentialStore>(sp =>
                new SecureDeviceCredentialStore(sec, () => sp.GetRequiredService<IAuthService>().CurrentBinding));
            sc.AddSingleton<IAuthService>(sp =>
                new AuthService(
                    sp.GetRequiredService<IAuthSettingsStore>(),
                    sp.GetRequiredService<IMsalClientAdapter>(),
                    initialSettings: null,
                    httpClient: null,
                    credentialStore: sp.GetRequiredService<IDeviceCredentialStore>(),
                    deviceInfoProvider: sp.GetRequiredService<IDeviceInfoProvider>(),
                    compileTimeClientId: compileTimeClientId));
            sc.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<IAuthService>());
        }
        else
        {
            sc.AddWakeMeshAuthServices(settingsPath);
        }

        sc.AddSingleton<ISecureStorageProvider>(sec);
        sc.AddSingleton<IMsalClientAdapter>(adapter);
        if (device != null)
        {
            // Replace device info with fixed id so credential keys are stable across assertions.
            sc.AddSingleton<IDeviceInfoProvider>(device);
        }

        sc.AddTransient<SettingsViewModel>();
        return sc.BuildServiceProvider();
    }

    static AuthSettings Valid(string url = "https://api.example.test", string? androidClientId = null) => new()
    {
        ApiBaseUrl = url,
        AndroidClientId = androidClientId ?? CompiledAuthConstants.CompileTimeClientId,
        ApiClientId = Api
    };

    [Fact]
    public async Task A1_SecondLaunch_ActualDi_SettingsPageInit_RestoresSavedSettings()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        int restored = 0, blank = 0, n = 12, completed = 0;
        string last = "";
        for (int i = 0; i < n; i++)
        {
            var path = Tmp("wmt14_") + ".json";
            await new JsonAuthSettingsStore(path).SaveSettingsAsync(Valid());
            var delayed = new T14DelayedSettingsStore(path, delayMs: 80);
            var ui = new LooperContext();
            var run = ui.RunAsync(async () =>
            {
                var sp = Boot(path, new T14Adapter(), new T14CountingStorage(), settingsStore: delayed);
                // Production: app startup + Settings display both await InitializeAsync.
                await sp.GetRequiredService<IAuthService>().InitializeAsync();
                var vm = sp.GetRequiredService<SettingsViewModel>();
                await vm.InitializeAsync();
                var auth = sp.GetRequiredService<IAuthService>();
                var tok = await sp.GetRequiredService<IAccessTokenProvider>().GetAccessTokenAsync();
                if (string.IsNullOrEmpty(vm.ApiBaseUrl)) blank++;
                else restored++;
                last = $"vm.Url={(vm.ApiBaseUrl ?? "<null>")} state={auth.State} canSignIn={vm.CanSignIn} token={(tok ?? "<null>")} loads={delayed.LoadCalls}";
            });
            if (await Task.WhenAny(run, Task.Delay(5000)) == run)
            {
                await run;
                completed++;
            }
        }

        _o.WriteLine($"A1 completed={completed}/{n} restored={restored} blank={blank} last: {last}");
        Assert.Equal(n, completed);
        Assert.Equal(n, restored);
        Assert.Equal(0, blank);
    }

    [Fact]
    public async Task A2_SaveBeforeInitBlocked_AndBlankFormCannotWipeSavedSettings()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        var path = Tmp("wmt14_") + ".json";
        var store = new JsonAuthSettingsStore(path);
        await store.SaveSettingsAsync(Valid("https://keep.example.test"));
        var delayed = new T14DelayedSettingsStore(path, delayMs: 200);

        var ui = new LooperContext();
        await ui.RunAsync(async () =>
        {
            var sp = Boot(path, new T14Adapter(), new T14CountingStorage(), settingsStore: delayed);
            var vm = sp.GetRequiredService<SettingsViewModel>();
            // User taps 保存 on blank form before init completes — must not wipe disk.
            Assert.False(vm.IsSettingsReady);
            await vm.SaveSettingsCommand.ExecuteAsync(null);

            var mid = await store.LoadSettingsAsync();
            Assert.Equal("https://keep.example.test", mid.ApiBaseUrl);

            await vm.InitializeAsync();
            Assert.True(vm.IsSettingsReady);
            Assert.Equal("https://keep.example.test", vm.ApiBaseUrl);

            // Saving the restored values keeps them.
            await vm.SaveSettingsCommand.ExecuteAsync(null);
        });

        var after = await store.LoadSettingsAsync();
        _o.WriteLine($"A2 after: url={after.ApiBaseUrl ?? "<null>"}");
        Assert.Equal("https://keep.example.test", after.ApiBaseUrl);
    }

    [Fact]
    public async Task A3_PersistedHomeAccount_RestoredViaGetAccountAsync_NoFirstOrDefault_CacheMissIsReauthRequired()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        var path = Tmp("wmt14_") + ".json";
        var sec = new T14CountingStorage();
        var device = new T14FixedDevice();

        // Process 1: sign in, persist home account id with settings.
        {
            var ad = new T14Adapter { NextAccount = "home-account-A" };
            var sp = Boot(path, ad, sec, device: device);
            var vm = sp.GetRequiredService<SettingsViewModel>();
            await vm.InitializeAsync();
            vm.ApiBaseUrl = "https://api.example.test";
            vm.AndroidClientId = CompiledAuthConstants.CompileTimeClientId;
            vm.ApiClientId = Api;
            await vm.SaveSettingsCommand.ExecuteAsync(null);
            await vm.SignInCommand.ExecuteAsync(null);
            Assert.Equal("home-account-A", ad.SelectedHomeAccountId);
            await sp.DisposeAsync();
        }

        // Process 2: fresh adapter (in-memory selected cleared) — restore from local persistence only.
        {
            var ad2 = new T14Adapter { Selected = null, CacheHasSelected = true };
            var sp2 = Boot(path, ad2, sec, device: device);
            var auth2 = sp2.GetRequiredService<IAuthService>();
            await auth2.InitializeAsync();
            Assert.Equal("home-account-A", ad2.SelectedHomeAccountId);
            Assert.Equal(AuthState.SignedIn, auth2.State);
            var tok = await auth2.GetAccessTokenAsync();
            Assert.Equal("tok-home-account-A", tok);
            await sp2.DisposeAsync();
        }

        // Process 3: persisted id present but MSAL cache miss → honest ReauthRequired (no FirstOrDefault).
        {
            var ad3 = new T14Adapter { Selected = null, CacheHasSelected = false };
            var sp3 = Boot(path, ad3, sec, device: device);
            var auth3 = sp3.GetRequiredService<IAuthService>();
            await auth3.InitializeAsync();
            Assert.Equal("home-account-A", ad3.SelectedHomeAccountId);
            Assert.Equal(AuthState.ReauthRequired, auth3.State);
            var tok3 = await auth3.GetAccessTokenAsync();
            Assert.Null(tok3);
            Assert.Equal(AuthState.ReauthRequired, auth3.State);
            _o.WriteLine($"A3 cache-miss state={auth3.State} silentCalls={ad3.Silent}");
            await sp3.DisposeAsync();
        }

        // Real adapter, no persisted account: silent is UiRequired; auth stays without fabricated account.
        {
            var real = new MsalClientAdapter(CompiledAuthConstants.CompileTimeClientId);
            Exception? ex = null;
            try
            {
                await real.AcquireTokenSilentAsync(new[] { $"api://{Api}/access_as_user" });
            }
            catch (Exception e)
            {
                ex = e;
            }

            Assert.IsType<MsalUiRequiredException>(ex);
            Assert.Null(real.SelectedHomeAccountId);
            _o.WriteLine($"A3 real-adapter ex={ex.GetType().Name}");
        }
    }

    [Fact]
    public async Task B1_UrlChange_InvalidatesToken_RemovesOldCredential_NoReviveOnOldUrl()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        var path = Tmp("wmt14_") + ".json";
        var ad = new T14Adapter();
        var sec = new T14CountingStorage();
        var device = new T14FixedDevice();
        var sp = Boot(path, ad, sec, device: device);
        var auth = sp.GetRequiredService<IAuthService>();
        var cred = sp.GetRequiredService<IDeviceCredentialStore>();
        var vm = sp.GetRequiredService<SettingsViewModel>();
        await vm.InitializeAsync();

        vm.ApiBaseUrl = "https://old.example.test";
        vm.AndroidClientId = CompiledAuthConstants.CompileTimeClientId;
        vm.ApiClientId = Api;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        await vm.SignInCommand.ExecuteAsync(null);
        var dev = Guid.Parse(device.DeviceId);
        await cred.SaveCredentialAsync(dev, "cred-old-A");
        Assert.True(sec.D.Count >= 1);
        _o.WriteLine($"B1 signed in: state={auth.State} keys={sec.D.Count}");

        vm.ApiBaseUrl = "https://new.example.test";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        var tokNew = await auth.GetAccessTokenAsync();
        var credNew = await cred.GetCredentialAsync(dev);
        _o.WriteLine($"B1 after URL change: state={auth.State} token={tokNew ?? "<null>"} cred={credNew ?? "<null>"} keys={sec.D.Count} signOuts={ad.SignOuts}");
        Assert.Null(credNew);
        Assert.Null(tokNew);
        Assert.True(ad.SignOuts >= 1);
        Assert.True(auth.State is AuthState.SignedOut or AuthState.ReauthRequired or AuthState.NotConfigured);

        vm.ApiBaseUrl = "https://old.example.test";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        var revived = await cred.GetCredentialAsync(dev);
        _o.WriteLine($"B1 back to old URL: cred={revived ?? "<null>"} keys={sec.D.Count}");
        Assert.Null(revived);
    }

    [Fact]
    public async Task B2_AccountSwitch_SignOut_Clear_RemovesCredentials_AllowsSignOutAfterClear()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        var path = Tmp("wmt14_") + ".json";
        var ad = new T14Adapter();
        var sec = new T14CountingStorage();
        var device = new T14FixedDevice();
        var sp = Boot(path, ad, sec, device: device);
        var auth = sp.GetRequiredService<IAuthService>();
        var cred = sp.GetRequiredService<IDeviceCredentialStore>();
        var vm = sp.GetRequiredService<SettingsViewModel>();
        await vm.InitializeAsync();

        vm.ApiBaseUrl = "https://api.example.test";
        vm.AndroidClientId = CompiledAuthConstants.CompileTimeClientId;
        vm.ApiClientId = Api;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        await vm.SignInCommand.ExecuteAsync(null);
        var dev = Guid.Parse(device.DeviceId);
        await cred.SaveCredentialAsync(dev, "cred-A");
        Assert.NotNull(await cred.GetCredentialAsync(dev));

        ad.NextAccount = "B";
        await auth.SignInInteractiveAsync();
        var afterSwitch = await cred.GetCredentialAsync(dev);
        _o.WriteLine($"B2 switch A->B: cred={afterSwitch ?? "<null>"} token={await auth.GetAccessTokenAsync()}");
        Assert.Null(afterSwitch);

        ad.NextAccount = "A";
        await auth.SignInInteractiveAsync();
        await cred.SaveCredentialAsync(dev, "cred-A2");
        await vm.SignOutCommand.ExecuteAsync(null);
        _o.WriteLine($"B2 after SignOut: keys={sec.D.Count} state={auth.State} token={(await auth.GetAccessTokenAsync()) ?? "<null>"}");
        Assert.Empty(sec.D);
        Assert.Null(await auth.GetAccessTokenAsync());

        ad.NextAccount = "A";
        await auth.SignInInteractiveAsync();
        Assert.Null(await cred.GetCredentialAsync(dev));

        await vm.ClearSettingsCommand.ExecuteAsync(null);
        _o.WriteLine($"B2 after Clear: keys={sec.D.Count} state={auth.State} signOuts={ad.SignOuts} canSignOut={vm.CanSignOut}");
        Assert.Empty(sec.D);
        Assert.True(vm.CanSignOut);
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.True(ad.SignOuts >= 2);
    }

    /// <summary>
    /// Zero compile-time client id is exercised only via injected AuthService ctor arg —
    /// test assembly CompiledAuthConstants default stays fictional nonzero (no MSBuild -p required).
    /// Configured nonzero runtime + injected zero compile-time → NeedsRebuild (zero never matches for login).
    /// </summary>
    [Fact]
    public async Task Z1_InjectedZeroCompileTimeClientId_ConfiguredRuntime_IsNeedsRebuild_NotSignInPath()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        var path = Tmp("wmt14_") + ".json";
        // Runtime settings use the test-default nonzero id; compile-time is injected as zero only.
        await new JsonAuthSettingsStore(path).SaveSettingsAsync(Valid());
        var ad = new T14Adapter();
        var sec = new T14CountingStorage();
        var sp = Boot(
            path,
            ad,
            sec,
            settingsStore: new JsonAuthSettingsStore(path),
            device: new T14FixedDevice(),
            compileTimeClientId: ZeroClientId);

        var auth = sp.GetRequiredService<IAuthService>();
        await auth.InitializeAsync();
        var vm = sp.GetRequiredService<SettingsViewModel>();
        await vm.InitializeAsync();

        _o.WriteLine(
            $"Z1 injected-zero: compileDefault={CompiledAuthConstants.CompileTimeClientId} " +
            $"runtime={auth.Settings.AndroidClientId} state={auth.State} canSignIn={auth.CanSignIn} status={auth.StatusMessage}");

        Assert.Equal(CompiledAuthConstants.CompileTimeClientId, auth.Settings.AndroidClientId);
        Assert.NotEqual(ZeroClientId, CompiledAuthConstants.CompileTimeClientId);
        Assert.False(auth.Settings.MatchesCompileTimeClientId(ZeroClientId));
        Assert.Equal(AuthState.NeedsRebuild, auth.State);
        Assert.False(auth.CanSignIn);
        Assert.False(vm.CanSignIn);
        Assert.Contains("再ビルド", auth.StatusMessage);
        Assert.Contains(ZeroClientId, auth.StatusMessage);
        Assert.Equal(0, ad.Interactive);
        Assert.False(await auth.SignInInteractiveAsync());
        Assert.Equal(0, ad.Interactive);

        await sp.DisposeAsync();
    }

    /// <summary>
    /// Shared DI path must wire AuthService to CompiledAuthConstants (nonzero test default) without extra MSBuild args.
    /// </summary>
    [Fact]
    public async Task Z2_SharedDi_DefaultCompileTime_MatchesConstants_AllowsConfiguredSignInPath()
    {
        AssertTestCompileTimeClientIdIsNonZeroFictional();

        var path = Tmp("wmt14_") + ".json";
        await new JsonAuthSettingsStore(path).SaveSettingsAsync(Valid());
        var ad = new T14Adapter { NextAccount = "di-account" };
        var sec = new T14CountingStorage();
        // Production-like registration: AddWakeMeshAuthServices + adapter override (no compileTime override).
        var sp = Boot(path, ad, sec, device: new T14FixedDevice());

        var auth = sp.GetRequiredService<IAuthService>();
        await auth.InitializeAsync();
        Assert.Equal(AuthState.SignedOut, auth.State);
        Assert.True(auth.CanSignIn);
        Assert.True(auth.Settings.MatchesCompileTimeClientId(CompiledAuthConstants.CompileTimeClientId));

        var signedIn = await auth.SignInInteractiveAsync();
        Assert.True(signedIn);
        Assert.Equal(AuthState.SignedIn, auth.State);
        Assert.Equal("tok-di-account", await auth.GetAccessTokenAsync());
        _o.WriteLine(
            $"Z2 shared-DI nonzero: clientId={CompiledAuthConstants.CompileTimeClientId} " +
            $"state={auth.State} silent={ad.Silent} interactive={ad.Interactive}");

        await sp.DisposeAsync();
    }
}
