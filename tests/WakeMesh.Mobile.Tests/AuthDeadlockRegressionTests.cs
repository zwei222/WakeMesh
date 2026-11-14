using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Client;
using WakeMesh.Mobile;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WakeMesh.Mobile.Tests;

// Emulates the Android main-looper SynchronizationContext (single UI thread).
internal sealed class LooperContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _q = new();
    private readonly Thread _t;
    public LooperContext()
    {
        _t = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (cb, st) in _q.GetConsumingEnumerable()) cb(st);
        }) { IsBackground = true, Name = "fake-ui" };
        _t.Start();
    }
    public override void Post(SendOrPostCallback d, object? state) => _q.Add((d, state));
    public Task RunAsync(Func<Task> f)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ => { try { await f(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }, null);
        return tcs.Task;
    }
    public void Dispose() => _q.CompleteAdding();
}

public sealed class ScopeRecordingAdapter : IMsalClientAdapter
{
    public System.Collections.Generic.List<string> Scopes { get; } = new();
    public int Interactive;
    public int SignOutCalls;
    public bool HasAccount = false;
    public string? BoundClientId;
    public string? SelectedHomeAccountId { get; private set; }

    public void UpdateClientId(string? clientId)
    {
        BoundClientId = clientId;
    }

    public void SetSelectedHomeAccountId(string? homeAccountId)
    {
        SelectedHomeAccountId = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId;
        if (SelectedHomeAccountId == null)
        {
            HasAccount = false;
        }
    }

    public Task<string?> AcquireTokenSilentAsync(string[] s, CancellationToken c = default)
    {
        Scopes.AddRange(s);
        return Task.FromResult<string?>("tok");
    }

    public Task<string?> AcquireTokenInteractiveAsync(string[] s, CancellationToken c = default)
    {
        Interactive++;
        Scopes.AddRange(s);
        HasAccount = true;
        SelectedHomeAccountId ??= "scope-account";
        return Task.FromResult<string?>("tok");
    }

    public Task SignOutAsync(CancellationToken c = default)
    {
        SignOutCalls++;
        HasAccount = false;
        SelectedHomeAccountId = null;
        return Task.CompletedTask;
    }

    public Task<bool> HasAccountAsync(CancellationToken c = default) =>
        Task.FromResult(HasAccount && !string.IsNullOrEmpty(SelectedHomeAccountId));
}

public sealed class AuthDeadlockRegressionTests
{
    private readonly ITestOutputHelper _o;
    public AuthDeadlockRegressionTests(ITestOutputHelper o) => _o = o;

    private static (IServiceProvider sp, JsonAuthSettingsStore store, IMsalClientAdapter adapter) WireWithDi(string path, IMsalClientAdapter? adapter = null)
    {
        var services = new ServiceCollection();
        var store = new JsonAuthSettingsStore(path);
        var msalAdapter = adapter ?? new MsalClientAdapter();
        var secStorage = new TestSecureStorageProvider();

        services.AddSingleton<IAuthSettingsStore>(store);
        services.AddSingleton<IMsalClientAdapter>(msalAdapter);
        services.AddSingleton<ISecureStorageProvider>(secStorage);
        services.AddSingleton<IDeviceCredentialStore>(sp =>
            new SecureDeviceCredentialStore(secStorage, () => sp.GetRequiredService<IAuthService>().CurrentBinding));
        services.AddSingleton<IAuthService>(sp =>
            new AuthService(
                sp.GetRequiredService<IAuthSettingsStore>(),
                sp.GetRequiredService<IMsalClientAdapter>(),
                initialSettings: null,
                httpClient: null,
                credentialStore: sp.GetRequiredService<IDeviceCredentialStore>()));
        services.AddTransient<SettingsViewModel>();

        var provider = services.BuildServiceProvider();
        return (provider, store, msalAdapter);
    }

    [Fact]
    public async Task D1_SaveValidSettings_OnUiThread_MustCompleteWithoutDeadlock()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmdl_" + Guid.NewGuid().ToString("N") + ".json");
        var (sp, _, _) = WireWithDi(path);
        using var ui = new LooperContext();
        var run = ui.RunAsync(async () =>
        {
            var vm = sp.GetRequiredService<SettingsViewModel>();
            await vm.InitializeAsync();
            vm.ApiBaseUrl = "https://api.example.test";
            vm.AndroidClientId = CompiledAuthConstants.CompileTimeClientId;
            vm.ApiClientId = "22222222-2222-2222-2222-222222222222";
            await vm.SaveSettingsCommand.ExecuteAsync(null);
        });
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))) == run;
        _o.WriteLine($"D1 Save completed within 3s: {done}");
        Assert.True(done, "SaveSettings must not deadlock on UI thread!");
    }

    [Fact]
    public async Task D2_AuthServiceLaunch_OnUiThread_MustCompleteWithoutDeadlock()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmdl_" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonAuthSettingsStore(path);
        await store.SaveSettingsAsync(new AuthSettings
        {
            ApiBaseUrl = "https://api.example.test",
            AndroidClientId = CompiledAuthConstants.CompileTimeClientId,
            ApiClientId = "22222222-2222-2222-2222-222222222222",
        });

        using var ui = new LooperContext();
        var run = ui.RunAsync(async () =>
        {
            var (sp, _, _) = WireWithDi(path);
            var auth = sp.GetRequiredService<IAuthService>();
            await auth.InitializeAsync();
        });
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))) == run;
        _o.WriteLine($"D2 Launch completed within 3s: {done}");
        Assert.True(done, "AuthService launch must not deadlock on UI thread when settings exist!");
    }

    [Fact]
    public async Task D3_SignIn_OnUiThread_MustCompleteWithoutDeadlock()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmdl_" + Guid.NewGuid().ToString("N") + ".json");
        var adapter = new ScopeRecordingAdapter();
        var (sp, store, _) = WireWithDi(path, adapter);
        await store.SaveSettingsAsync(new AuthSettings
        {
            ApiBaseUrl = "https://api.example.test",
            AndroidClientId = CompiledAuthConstants.CompileTimeClientId,
            ApiClientId = "22222222-2222-2222-2222-222222222222",
        });

        using var ui = new LooperContext();
        var run = ui.RunAsync(async () =>
        {
            var auth = sp.GetRequiredService<IAuthService>();
            await auth.InitializeAsync();
            var vm = sp.GetRequiredService<SettingsViewModel>();
            await vm.InitializeAsync();
            if (vm.CanSignIn)
            {
                await vm.SignInCommand.ExecuteAsync(null);
            }
        });
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))) == run;
        _o.WriteLine($"D3 SignIn completed within 3s: {done}");
        Assert.True(done, "SignInInteractiveAsync must not deadlock on UI thread!");
    }

    [Fact]
    public async Task P1_CustomScopeRemoved_ScopeStrictlyApiAccessAsUser()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmprobe_" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonAuthSettingsStore(path);
        var adapter = new ScopeRecordingAdapter();
        var validClientId = "11111111-1111-1111-1111-111111111111";
        var auth = new AuthService(store, adapter, null, null, null, null, validClientId);
        var vm = new SettingsViewModel(auth, store);
        await vm.InitializeAsync();
        vm.ApiBaseUrl = "https://api.example.test";
        vm.AndroidClientId = validClientId;
        vm.ApiClientId = "22222222-2222-2222-2222-222222222222";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Null(vm.ValidationMessage);
        Assert.Equal("api://22222222-2222-2222-2222-222222222222/access_as_user", auth.Settings.EffectiveScope);

        // Verify that only the strictly formatted api://{ApiClientId}/access_as_user scope is requested
        await auth.SignInInteractiveAsync();
        await auth.GetAccessTokenAsync();
        Assert.All(adapter.Scopes, s => Assert.Equal("api://22222222-2222-2222-2222-222222222222/access_as_user", s));
    }

    [Fact]
    public async Task P2_PlaceholderZeroGuid_LoginRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmprobe_" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonAuthSettingsStore(path);
        var adapter = new ScopeRecordingAdapter();
        var auth = new AuthService(store, adapter);
        var vm = new SettingsViewModel(auth, store);
        await vm.InitializeAsync();
        vm.ApiBaseUrl = "https://api.example.test";
        vm.AndroidClientId = "00000000-0000-0000-0000-000000000000";
        vm.ApiClientId = "00000000-0000-0000-0000-000000000000";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Zero GUID is unconfigured: CanSignIn must be false, SignInInteractive must return false
        Assert.False(vm.CanSignIn);
        Assert.False(auth.CanSignIn);
        var signInResult = await auth.SignInInteractiveAsync();
        Assert.False(signInResult);
        Assert.Equal(0, adapter.Interactive);
    }

    [Fact]
    public async Task P3_ApiUrlChange_And_SignOut_IsolatesAndUnbindsCredential()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmprobe_" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonAuthSettingsStore(path);
        var adapter = new ScopeRecordingAdapter { HasAccount = true };
        var sec = new TestSecureStorageProvider();
        var validClientId = "11111111-1111-1111-1111-111111111111";
        IAuthService? authRef = null;
        var cred = new SecureDeviceCredentialStore(sec, () => authRef?.CurrentBinding);
        var auth = new AuthService(store, adapter, null, null, cred, null, validClientId);
        authRef = auth;

        var vm = new SettingsViewModel(auth, store);
        await vm.InitializeAsync();
        vm.ApiBaseUrl = "https://old-api.example.test";
        vm.AndroidClientId = validClientId;
        vm.ApiClientId = "22222222-2222-2222-2222-222222222222";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var deviceId = Guid.NewGuid();
        await cred.SaveCredentialAsync(deviceId, "cred-issued-by-old-api-for-accountA");

        // User points app at a different API host
        vm.ApiBaseUrl = "https://other-host.example.test";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Under new API origin, old credential must not be returned / leaked to new API
        var credAfterUrlChange = await cred.GetCredentialAsync(deviceId);
        Assert.Null(credAfterUrlChange);

        // Sign out must remove MSAL accounts and allow sign-in cleanly
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.False(adapter.HasAccount);
        Assert.True(adapter.SignOutCalls >= 1);
    }

    [Fact]
    public async Task P4_ClearSettings_SignsOutMsalAccount_AndAllowsSignOut()
    {
        var path = Path.Combine(Path.GetTempPath(), "wmprobe_" + Guid.NewGuid().ToString("N") + ".json");
        var store = new JsonAuthSettingsStore(path);
        var adapter = new ScopeRecordingAdapter { HasAccount = true, };
        adapter.SetSelectedHomeAccountId("pre-existing");
        adapter.HasAccount = true;
        var auth = new AuthService(store, adapter);
        var vm = new SettingsViewModel(auth, store);
        await vm.InitializeAsync();
        vm.ApiBaseUrl = "https://api.example.test";
        vm.AndroidClientId = CompiledAuthConstants.CompileTimeClientId;
        vm.ApiClientId = "22222222-2222-2222-2222-222222222222";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(adapter.HasAccount);

        // Clear settings
        await vm.ClearSettingsCommand.ExecuteAsync(null);

        // MSAL accounts must be signed out/removed on clear
        Assert.False(adapter.HasAccount);
        Assert.True(adapter.SignOutCalls >= 1);
        // CanSignOut remains valid even after settings cleared
        Assert.True(vm.CanSignOut);
    }

    [Fact]
    public void P5_GuidFormatMismatch_CaseAndBracesNormalized()
    {
        var bracedId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        var s = new AuthSettings { AndroidClientId = bracedId };

        // Normalized to lowercase 'd' canonical format without braces
        Assert.NotNull(s.AndroidClientId);
        Assert.DoesNotContain("{", s.AndroidClientId);
        Assert.DoesNotContain("}", s.AndroidClientId);
        Assert.Equal(s.AndroidClientId.ToLowerInvariant(), s.AndroidClientId);

        // Matches when compile time is same GUID
        Assert.True(s.MatchesCompileTimeClientId(bracedId));
    }
}
