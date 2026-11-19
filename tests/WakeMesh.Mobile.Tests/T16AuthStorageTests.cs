using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T16 permanent regressions:
/// 1. Strict OS-cancel-then-delete in <see cref="AlarmCoordinator"/> (no silent success on cancel failure).
/// 3. Binding-scoped credential clearing by server-device-id index (never local deviceId only),
///    with AuthService clearing every old-binding credential before sign-out.
/// 4. Auth settings JSON: atomic flush+rename, full-path exclusive gate, corrupt file is a typed warning
///    (never silently reported as plain "未設定"), and never clobbered by a home-account-only save.
/// </summary>
public sealed class T16AuthStorageTests
{
    private const string Api = "22222222-2222-2222-2222-222222222222";

    private static string TmpDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmt16_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AuthSettings Valid(string url = "https://api.example.test", string? androidClientId = null) => new()
    {
        ApiBaseUrl = url,
        AndroidClientId = androidClientId ?? CompiledAuthConstants.CompileTimeClientId,
        ApiClientId = Api
    };

    // ---------------------------------------------------------------------
    // 4. Auth settings: corrupt JSON must raise a typed error (no silent empty)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AuthSettings_CorruptJson_RaisesTypedError_NotSilentUnconfigured()
    {
        var dir = TmpDir();
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        var store = new JsonAuthSettingsStore(path);

        var ex = await Assert.ThrowsAsync<CorruptedAuthSettingsException>(() => store.LoadSettingsAsync());
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(AuthSettingsLoadStatus.Corrupted, store.LastLoadDiagnostic!.Status);
        Assert.True(store.LastLoadDiagnostic.HasWarning);
    }

    [Fact]
    public async Task AuthService_CorruptSettings_TypedWarningState_NotPlainUnconfigured()
    {
        var dir = TmpDir();
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        var adapter = new T14Adapter();
        var auth = new AuthService(
            new JsonAuthSettingsStore(path),
            adapter,
            compileTimeClientId: CompiledAuthConstants.CompileTimeClientId);

        await auth.InitializeAsync();

        // AuthState enum lives in a file outside this task's write scope: the typed warning is exposed
        // through SettingsLoadDiagnostic instead of a silent NotConfigured.
        Assert.Equal(AuthState.NotConfigured, auth.State);
        Assert.NotNull(auth.SettingsLoadDiagnostic);
        Assert.Equal(AuthSettingsLoadStatus.Corrupted, auth.SettingsLoadDiagnostic!.Status);
        Assert.Contains("破損", auth.StatusMessage);
        Assert.False(auth.CanSignIn);
        Assert.Equal(0, adapter.Interactive);

        // A corrupt settings file must never be echoed into diagnostics (no secret material).
        Assert.DoesNotContain("{ this is not json", auth.StatusMessage);
    }

    [Fact]
    public async Task AuthSettings_HomeAccountOnlySave_OnCorruptFile_RaisesTypedError_AndDoesNotClobber()
    {
        var dir = TmpDir();
        var path = Path.Combine(dir, "settings.json");
        const string corrupt = "{ this is not json ";
        await File.WriteAllTextAsync(path, corrupt);

        var store = new JsonAuthSettingsStore(path);

        await Assert.ThrowsAsync<CorruptedAuthSettingsException>(
            () => store.SaveSelectedHomeAccountIdAsync("home-account-A"));

        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AuthSettings_Save_IsAtomic_NoTempLeftovers_AndTwoInstancesSerialize()
    {
        var dir = TmpDir();
        var path = Path.Combine(dir, "settings.json");
        var a = new JsonAuthSettingsStore(path);
        var b = new JsonAuthSettingsStore(path);

        var writes = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var store = i % 2 == 0 ? a : b;
            writes.Add(store.SaveSettingsAsync(Valid(url: $"https://api{i}.example.test")));
        }

        await Task.WhenAll(writes);

        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(dir));

        var loaded = await a.LoadSettingsAsync();
        Assert.StartsWith("https://api", loaded.ApiBaseUrl);
        Assert.Equal(Api, loaded.ApiClientId);
        Assert.True(loaded.Validate().IsConfigured);
    }

    // ---------------------------------------------------------------------
    // 3. Credentials: clear by binding (server device id index), never local deviceId
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CredentialStore_ClearForBinding_RemovesEveryServerDeviceId_AndDropsIndex()
    {
        var storage = new T14CountingStorage();
        var binding = new AccountBinding("https://api.example.test", Api, CompiledAuthConstants.CompileTimeClientId, "home-A");
        var store = new SecureDeviceCredentialStore(storage, () => binding);

        var dev1 = Guid.NewGuid();
        var dev2 = Guid.NewGuid();
        await store.SaveCredentialAsync(dev1, "cred-1");
        await store.SaveCredentialAsync(dev2, "cred-2");

        Assert.Equal("cred-1", await store.GetCredentialAsync(dev1));
        Assert.Equal("cred-2", await store.GetCredentialAsync(dev2));

        var cleared = await store.ClearCredentialsForBindingAsync(binding);

        Assert.Equal(2, cleared);
        Assert.Null(await store.GetCredentialAsync(dev1));
        Assert.Null(await store.GetCredentialAsync(dev2));
        Assert.DoesNotContain(storage.D.Keys, k => k.StartsWith("wakemesh_device_cred", StringComparison.Ordinal));

        // Other bindings stay isolated.
        var otherBinding = new AccountBinding("https://other.example.test", Api, CompiledAuthConstants.CompileTimeClientId, "home-A");
        var otherStore = new SecureDeviceCredentialStore(storage, () => otherBinding);
        await otherStore.SaveCredentialAsync(dev1, "other-cred");
        Assert.Equal(0, await store.ClearCredentialsForBindingAsync(binding)); // binding index already purged
        Assert.Equal("other-cred", await otherStore.GetCredentialAsync(dev1));
    }

    [Fact]
    public async Task AuthService_SignOut_RemovesAllOldBindingCredentials_AndOldUrlNeverRevives()
    {
        var dir = TmpDir();
        var path = Path.Combine(dir, "settings.json");
        var storage = new T14CountingStorage();
        var adapter = new T14Adapter { NextAccount = "home-A" };
        var device = new T14FixedDevice();

        var settingsStore = new JsonAuthSettingsStore(path);
        AuthService? authRef = null;
        var credentialStore = new SecureDeviceCredentialStore(storage, () => authRef!.CurrentBinding);
        var auth = new AuthService(
            settingsStore,
            adapter,
            initialSettings: null,
            httpClient: null,
            credentialStore: credentialStore,
            deviceInfoProvider: device,
            compileTimeClientId: CompiledAuthConstants.CompileTimeClientId);
        authRef = auth;

        await auth.InitializeAsync();
        await auth.ApplySettingsAsync(Valid("https://old.example.test"));
        Assert.True(await auth.SignInInteractiveAsync());
        Assert.Equal(AuthState.SignedIn, auth.State);

        // Two server device ids were registered under this one binding (e.g. re-registration + retire).
        var serverDev1 = Guid.NewGuid();
        var serverDev2 = Guid.NewGuid();
        await credentialStore.SaveCredentialAsync(serverDev1, "cred-1");
        await credentialStore.SaveCredentialAsync(serverDev2, "cred-2");
        Assert.Equal("cred-1", await credentialStore.GetCredentialAsync(serverDev1));

        await auth.SignOutAsync();

        Assert.Null(await credentialStore.GetCredentialAsync(serverDev1));
        Assert.Null(await credentialStore.GetCredentialAsync(serverDev2));
        Assert.DoesNotContain(storage.D.Keys, k => k.StartsWith("wakemesh_device_cred", StringComparison.Ordinal));
        Assert.Equal(AuthState.SignedOut, auth.State);

        // Re-applying the same old URL must not revive any credential.
        await auth.ApplySettingsAsync(Valid("https://old.example.test"));
        Assert.Null(await credentialStore.GetCredentialAsync(serverDev1));
        Assert.Null(await credentialStore.GetCredentialAsync(serverDev2));
    }

    [Fact]
    public async Task CredentialStore_CorruptIndex_RaisesTypedError_InsteadOfPretendingNothingToClear()
    {
        var storage = new T14CountingStorage();
        var binding = new AccountBinding("https://api.example.test", Api, CompiledAuthConstants.CompileTimeClientId, "home-A");
        var store = new SecureDeviceCredentialStore(storage, () => binding);

        // Index present but unreadable: the store cannot prove a complete cleanup.
        await storage.SetAsync(
            $"wakemesh_device_cred_index_{binding.GetBindingKey()}",
            "{ not a valid index ");

        await Assert.ThrowsAsync<CredentialIndexCorruptedException>(() => store.ClearCredentialsForBindingAsync(binding));
    }

    [Fact]
    public async Task AuthSettings_CorruptFile_UserResave_RecoversWarningState()
    {
        var dir = TmpDir();
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        var store = new JsonAuthSettingsStore(path);
        var adapter = new T14Adapter();
        var auth = new AuthService(store, adapter, compileTimeClientId: CompiledAuthConstants.CompileTimeClientId);
        await auth.InitializeAsync();
        Assert.True(auth.SettingsLoadDiagnostic!.HasWarning);

        // An explicit user save replaces the broken document and clears the typed warning.
        await auth.ApplySettingsAsync(Valid());

        Assert.Null(auth.SettingsLoadDiagnostic);
        Assert.Equal(AuthState.SignedOut, auth.State);
        Assert.Contains("サインアウト", auth.StatusMessage);
        Assert.True(auth.CanSignIn);
        Assert.Equal(Api, (await store.LoadSettingsAsync()).ApiClientId);
    }

    // ---------------------------------------------------------------------
    // 1. Strict cancel-then-delete
    // ---------------------------------------------------------------------

    private sealed class FailingCancelScheduler : IAlarmScheduler
    {
        private readonly HashSet<string> _scheduled = new(StringComparer.Ordinal);
        public bool FailCancel { get; set; }
        public int CancelCalls { get; private set; }

        public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
        {
            _scheduled.Add(alarm.Id);
            return Task.CompletedTask;
        }

        public Task CancelAlarmAsync(string alarmId)
        {
            CancelCalls++;
            if (FailCancel)
            {
                throw new InvalidOperationException("Simulated OS cancel failure");
            }

            _scheduled.Remove(alarmId);
            return Task.CompletedTask;
        }

        public bool Contains(string id) => _scheduled.Contains(id);
    }

    [Fact]
    public async Task Coordinator_StrictCancelThenDelete_PropagatesCancelFailure_AndKeepsStoredAlarm()
    {
        var dir = TmpDir();
        var scheduler = new FailingCancelScheduler();
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new JapaneseHolidayService()),
            scheduler);

        var alarm = new LocalAlarmModel { Id = "sync-deadbeefdeadbeef-" + Guid.NewGuid().ToString("N"), Time = new TimeOnly(7, 0), IsEnabled = true };
        await coordinator.SaveAndScheduleAsync(alarm);
        Assert.True(scheduler.Contains(alarm.Id));

        scheduler.FailCancel = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CancelThenDeleteAsync(alarm.Id));

        // OS cancellation failed → the alarm must still exist in the store (retry can recover it).
        Assert.NotNull(await coordinator.GetByIdAsync(alarm.Id));

        scheduler.FailCancel = false;
        await coordinator.CancelThenDeleteAsync(alarm.Id);

        Assert.Null(await coordinator.GetByIdAsync(alarm.Id));
        Assert.False(scheduler.Contains(alarm.Id));
    }

    [Fact]
    public async Task Coordinator_ExistingBestEffortDelete_IsUnchanged_ForUiDeletePath()
    {
        var dir = TmpDir();
        var scheduler = new FailingCancelScheduler { FailCancel = true };
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new JapaneseHolidayService()),
            scheduler);

        await coordinator.SaveAndScheduleAsync(new LocalAlarmModel { Id = "manual-1", Time = new TimeOnly(8, 0), IsEnabled = true });

        // Existing UI delete contract kept as-is: best-effort OS cancel, store delete always happens.
        await coordinator.DeleteAndCancelAsync("manual-1");
        Assert.Null(await coordinator.GetByIdAsync("manual-1"));
    }
}
