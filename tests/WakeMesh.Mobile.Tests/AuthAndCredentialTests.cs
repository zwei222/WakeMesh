using System;
using System.IO;
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

namespace WakeMesh.Mobile.Tests;

public sealed class AuthAndCredentialTests
{
    // =========================================================================
    // 1. Settings Validation & Compile-Time Mismatch Gate
    // =========================================================================

    [Fact]
    public void AuthSettings_WhenEmpty_IsValidUnconfigured()
    {
        var settings = new AuthSettings();
        var result = settings.Validate();

        Assert.True(result.IsValid);
        Assert.False(result.IsConfigured);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void AuthSettings_WhenHttpInsteadOfHttps_ValidationFails()
    {
        var settings = new AuthSettings
        {
            ApiBaseUrl = "http://api.wakemesh.local",
            AndroidClientId = Guid.NewGuid().ToString(),
            ApiClientId = Guid.NewGuid().ToString()
        };

        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("https", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthSettings_WhenClientIdIsNotGuid_ValidationFails()
    {
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = "not-a-valid-guid",
            ApiClientId = Guid.NewGuid().ToString()
        };

        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("GUID", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthSettings_WhenValid_ValidationSucceeds_AndDefaultScopeIsFormed()
    {
        var androidId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = androidId.ToString(),
            ApiClientId = apiId.ToString()
        };

        var result = settings.Validate();

        Assert.True(result.IsValid);
        Assert.True(result.IsConfigured);
        Assert.Equal($"api://{apiId}/access_as_user", settings.EffectiveScope);
    }

    [Fact]
    public void AuthSettings_MismatchCompileTimeClientId_GateDisablesLogin()
    {
        // Runtime client ID is different from compile-time zero GUID
        var runtimeClientId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = runtimeClientId.ToString(),
            ApiClientId = Guid.NewGuid().ToString()
        };

        var store = new InMemoryAuthSettingsStore(settings);
        var mockMsal = new TestMsalClientAdapter();
        var authService = new AuthService(store, mockMsal);

        Assert.Equal(AuthState.NeedsRebuild, authService.State);
        Assert.False(authService.CanSignIn);
        Assert.Contains("再ビルド", authService.StatusMessage);
        Assert.Contains(CompiledAuthConstants.CompileTimeClientId, authService.StatusMessage);
    }

    [Fact]
    public void AuthSettings_MatchingCompileTimeClientId_AllowsSignIn()
    {
        // Matches compile-time client ID
        var compileId = Guid.NewGuid().ToString("d");
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = compileId,
            ApiClientId = Guid.NewGuid().ToString("d")
        };

        var store = new InMemoryAuthSettingsStore(settings);
        var mockMsal = new TestMsalClientAdapter();
        var authService = new AuthService(store, mockMsal, compileTimeClientId: compileId);

        Assert.Equal(AuthState.SignedOut, authService.State);
        Assert.True(authService.CanSignIn);
    }

    // =========================================================================
    // 2. IDeviceCredentialStore (SecureStorage paired storage & corruption handling)
    // =========================================================================

    [Fact]
    public async Task SecureDeviceCredentialStore_SavesAndRetrievesCredential_AsPairWithDeviceId()
    {
        var secureStorage = new TestSecureStorageProvider();
        IDeviceCredentialStore store = new SecureDeviceCredentialStore(secureStorage);

        var deviceId = Guid.NewGuid();
        var credential = "sec_test_credential_12345";

        await store.SaveCredentialAsync(deviceId, credential);
        var retrieved = await store.GetCredentialAsync(deviceId);

        Assert.Equal(credential, retrieved);

        // Verify stored under key containing deviceId
        var storedRaw = await secureStorage.GetAsync($"wakemesh_device_cred_{deviceId:D}");
        Assert.NotNull(storedRaw);
        Assert.Contains(deviceId.ToString(), storedRaw);
        Assert.Contains(credential, storedRaw);

        // Other device ID returns null
        var otherRetrieved = await store.GetCredentialAsync(Guid.NewGuid());
        Assert.Null(otherRetrieved);
    }

    [Fact]
    public async Task SecureDeviceCredentialStore_WhenCorruptedOrUndecryptable_ThrowsTypedException_NoPlaintextFallback()
    {
        var secureStorage = new TestSecureStorageProvider();
        IDeviceCredentialStore store = new SecureDeviceCredentialStore(secureStorage);

        var deviceId = Guid.NewGuid();
        // Simulate corrupted / unparseable payload or decryption error
        await secureStorage.SetAsync($"wakemesh_device_cred_{deviceId:D}", "{corrupted-garbage-not-json");

        // Must throw DeviceCredentialCorruptedException requesting re-registration; no plaintext fallback
        var ex = await Assert.ThrowsAsync<DeviceCredentialCorruptedException>(() => store.GetCredentialAsync(deviceId));
        Assert.Equal(deviceId, ex.DeviceId);
        Assert.Contains("再登録", ex.Message);

        // Corrupted entry should be removed
        var afterClean = await secureStorage.GetAsync($"wakemesh_device_cred_{deviceId:D}");
        Assert.Null(afterClean);
    }

    [Fact]
    public async Task SecureDeviceCredentialStore_WhenDecryptionThrowsException_ThrowsTypedException()
    {
        var secureStorage = new ThrowingSecureStorageProvider();
        IDeviceCredentialStore store = new SecureDeviceCredentialStore(secureStorage);

        var deviceId = Guid.NewGuid();

        // When KeyStore fails (e.g. backup restore with lost key)
        var ex = await Assert.ThrowsAsync<DeviceCredentialCorruptedException>(() => store.GetCredentialAsync(deviceId));
        Assert.Equal(deviceId, ex.DeviceId);
    }

    // =========================================================================
    // 3. Interaction Gate (Silent only in background sync, Interactive only from UI)
    // =========================================================================

    [Fact]
    public async Task IAccessTokenProvider_GetAccessTokenAsync_OnlyCallsSilent_TransitionsToReauthRequiredOnUiRequired()
    {
        var compileId = Guid.NewGuid().ToString("d");
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = compileId,
            ApiClientId = Guid.NewGuid().ToString("d")
        };

        var store = new InMemoryAuthSettingsStore(settings);
        var mockMsal = new TestMsalClientAdapter
        {
            ThrowUiRequiredOnSilent = true
        };
        // A cached account requiring fresh consent is different from a signed-out session.
        mockMsal.SetSelectedHomeAccountId("test-account");
        mockMsal.HasAccount = true;
        var authService = new AuthService(store, mockMsal, compileTimeClientId: compileId);
        IAccessTokenProvider provider = authService;

        // Background call
        var token = await provider.GetAccessTokenAsync();

        Assert.Null(token);
        Assert.Equal(AuthState.ReauthRequired, authService.State);
        Assert.Equal(0, mockMsal.InteractiveCallCount); // Never interactive in background!
        Assert.Equal(1, mockMsal.SilentCallCount);
    }

    [Fact]
    public async Task AuthService_SignInInteractive_OnlyCalledExplicitly_TransitionsToSignedIn()
    {
        var compileId = Guid.NewGuid().ToString("d");
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = compileId,
            ApiClientId = Guid.NewGuid().ToString("d")
        };

        var store = new InMemoryAuthSettingsStore(settings);
        var mockMsal = new TestMsalClientAdapter
        {
            InteractiveTokenToReturn = "test_bearer_token"
        };
        var authService = new AuthService(store, mockMsal, compileTimeClientId: compileId);

        Assert.Equal(AuthState.SignedOut, authService.State);

        var success = await authService.SignInInteractiveAsync();

        Assert.True(success);
        Assert.Equal(AuthState.SignedIn, authService.State);
        Assert.Equal(1, mockMsal.InteractiveCallCount);

        // Sign out
        await authService.SignOutAsync();
        Assert.Equal(AuthState.SignedOut, authService.State);
        Assert.Equal(1, mockMsal.SignOutCallCount);
    }

    [Fact]
    public async Task AuthService_SignInInteractive_WhenMismatch_DisablesLoginAndDoesNotInvokeMsal()
    {
        var settings = new AuthSettings
        {
            ApiBaseUrl = "https://api.wakemesh.local",
            AndroidClientId = Guid.NewGuid().ToString(), // Mismatch
            ApiClientId = Guid.NewGuid().ToString()
        };

        var store = new InMemoryAuthSettingsStore(settings);
        var mockMsal = new TestMsalClientAdapter();
        var authService = new AuthService(store, mockMsal);

        var success = await authService.SignInInteractiveAsync();

        Assert.False(success);
        Assert.Equal(AuthState.NeedsRebuild, authService.State);
        Assert.Equal(0, mockMsal.InteractiveCallCount);
    }

    // =========================================================================
    // 4. DI Unconfigured Startup (未設定時の起動継続 & 接続確認)
    // =========================================================================

    [Fact]
    public async Task WakeMeshCoreServices_WithUnconfiguredAuth_StartsUpAndAllowsLocalAlarmOperations()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var services = new ServiceCollection();
            var alarmsPath = Path.Combine(tempDir, "alarms.json");
            var deviceIdPath = Path.Combine(tempDir, "device_id.txt");
            var settingsPath = Path.Combine(tempDir, "auth_settings.json");

            services.AddWakeMeshCoreServices(alarmsPath, deviceIdPath);
            services.AddWakeMeshAuthServices(settingsPath);
            services.AddSingleton<IAlarmScheduler, StubAlarmScheduler>();
            services.AddSingleton<IPermissionService, StubPermissionService>();
            services.AddSingleton<IAlarmRingingService, StubAlarmRingingService>();
            services.AddSingleton<IAudioPlaybackService, StubAudioPlaybackService>();
            services.AddSingleton<ISecureStorageProvider, TestSecureStorageProvider>();
            services.AddSingleton<IMsalClientAdapter, TestMsalClientAdapter>();

            var sp = services.BuildServiceProvider();

            // Resolve services
            var authService = sp.GetRequiredService<IAuthService>();
            var coordinator = sp.GetRequiredService<AlarmCoordinator>();
            var tokenProvider = sp.GetRequiredService<IAccessTokenProvider>();
            var credStore = sp.GetRequiredService<IDeviceCredentialStore>();

            Assert.Equal(AuthState.NotConfigured, authService.State);

            // Token is null when unconfigured, no crash
            var token = await tokenProvider.GetAccessTokenAsync();
            Assert.Null(token);

            // Connection check returns unconfigured message without HTTP call
            var checkResult = await authService.CheckConnectionAsync();
            Assert.False(checkResult.IsConnected);
            Assert.Contains("未設定", checkResult.Message);

            // Local alarms work 100% fine
            var alarm = new LocalAlarmModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Time = new TimeOnly(8, 0),
                Name = "朝のアラーム",
                IsEnabled = true
            };
            await coordinator.SaveAndScheduleAsync(alarm);
            var alarms = await coordinator.GetAllAsync();
            Assert.Single(alarms);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    // =========================================================================
    // 5. Alarm Continuity during Auth / Re-Auth Failure
    // =========================================================================

    [Fact]
    public async Task AlarmCoordinator_ContinuesNormally_WhenAuthIsReauthRequiredOrCredentialCorrupted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var alarmsPath = Path.Combine(tempDir, "alarms.json");
            var deviceIdPath = Path.Combine(tempDir, "device_id.txt");
            var alarmStore = new JsonFileAlarmStore(alarmsPath);
            var scheduler = new StubAlarmScheduler();
            var coordinator = new AlarmCoordinator(
                alarmStore,
                new AlarmScheduleCalculator(new JapaneseHolidayService()),
                scheduler);

            // Create alarm while auth is broken / in re-auth state
            var created = new LocalAlarmModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Time = new TimeOnly(7, 30),
                Name = "Reauth Test Alarm",
                IsEnabled = true
            };
            await coordinator.SaveAndScheduleAsync(created);
            Assert.True(created.IsEnabled);

            // Schedule / fire / toggle / delete all succeed locally
            created.IsEnabled = false;
            await coordinator.SaveAndScheduleAsync(created);
            var list = await coordinator.GetAllAsync();
            Assert.Single(list);
            Assert.False(list[0].IsEnabled);

            created.IsEnabled = true;
            await coordinator.SaveAndScheduleAsync(created);
            var rescheduledCount = await coordinator.RescheduleAllActiveAsync();
            Assert.Equal(1, rescheduledCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    // =========================================================================
    // 6. SettingsViewModel Integration & UI Privacy
    // =========================================================================

    [Fact]
    public async Task SettingsViewModel_HandlesSave_Validation_AndPrivacy()
    {
        var settingsStore = new InMemoryAuthSettingsStore(new AuthSettings());
        var msalAdapter = new TestMsalClientAdapter();
        var authService = new AuthService(settingsStore, msalAdapter);

        var vm = new SettingsViewModel(authService, settingsStore);
        await vm.InitializeAsync();

        Assert.Equal("未設定", vm.AuthStateText);
        Assert.False(vm.CanSignIn);

        // Input invalid URL
        vm.ApiBaseUrl = "http://insecure.example.com";
        vm.AndroidClientId = Guid.NewGuid().ToString();
        vm.ApiClientId = Guid.NewGuid().ToString();

        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ValidationMessage);
        Assert.Contains("https", vm.ValidationMessage);

        // Input valid URL matching compile-time
        var compileId = Guid.NewGuid().ToString("d");
        var vmAuth = new AuthService(settingsStore, msalAdapter, compileTimeClientId: compileId);
        var vmWithCompileId = new SettingsViewModel(vmAuth, settingsStore);
        await vmWithCompileId.InitializeAsync();
        vmWithCompileId.ApiBaseUrl = "https://api.wakemesh.local";
        vmWithCompileId.AndroidClientId = compileId;
        vmWithCompileId.ApiClientId = Guid.NewGuid().ToString("d");

        await vmWithCompileId.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Null(vmWithCompileId.ValidationMessage);
        Assert.True(vmWithCompileId.CanSignIn);

        // Sign in
        msalAdapter.InteractiveTokenToReturn = "test_token";
        await vmWithCompileId.SignInCommand.ExecuteAsync(null);

        Assert.Equal("サインイン済み", vmWithCompileId.AuthStateText);
        // Privacy: no user email or name in viewmodel
        Assert.DoesNotContain("@", vmWithCompileId.AuthStateText);
        Assert.DoesNotContain("@", vmWithCompileId.StatusMessage);

        // Connection check
        await vmWithCompileId.CheckConnectionCommand.ExecuteAsync(null);
        Assert.NotNull(vmWithCompileId.ConnectionCheckResultText);
    }
}

// =============================================================================
// Test Doubles
// =============================================================================

public sealed class InMemoryAuthSettingsStore : IAuthSettingsStore
{
    private AuthSettings _settings;

    public InMemoryAuthSettingsStore(AuthSettings? initial = null)
    {
        _settings = initial ?? new AuthSettings();
    }

    public Task<AuthSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_settings);
    }

    public Task SaveSettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

public sealed class TestSecureStorageProvider : ISecureStorageProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dict = new();

    public Task<string?> GetAsync(string key)
    {
        _dict.TryGetValue(key, out var val);
        return Task.FromResult<string?>(val);
    }

    public Task SetAsync(string key, string value)
    {
        _dict[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key)
    {
        return Task.FromResult(_dict.TryRemove(key, out _));
    }

    public void RemoveAll()
    {
        _dict.Clear();
    }
}

public sealed class ThrowingSecureStorageProvider : ISecureStorageProvider
{
    public Task<string?> GetAsync(string key) => throw new System.Security.Cryptography.CryptographicException("Keystore key invalid");
    public Task SetAsync(string key, string value) => throw new System.Security.Cryptography.CryptographicException("Keystore key invalid");
    public Task<bool> RemoveAsync(string key) => Task.FromResult(true);
    public void RemoveAll() { }
}

public sealed class TestMsalClientAdapter : IMsalClientAdapter
{
    public int SilentCallCount { get; private set; }
    public int InteractiveCallCount { get; private set; }
    public int SignOutCallCount { get; private set; }

    public bool ThrowUiRequiredOnSilent { get; set; }
    public string? InteractiveTokenToReturn { get; set; }
    public bool HasAccount { get; set; }
    public string? SelectedHomeAccountId { get; private set; }

    public void SetSelectedHomeAccountId(string? homeAccountId)
    {
        SelectedHomeAccountId = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId;
        if (SelectedHomeAccountId == null)
        {
            HasAccount = false;
        }
    }

    public Task<string?> AcquireTokenSilentAsync(string[] scopes, CancellationToken cancellationToken = default)
    {
        SilentCallCount++;
        if (ThrowUiRequiredOnSilent)
        {
            throw new MsalUiRequiredException("user_null", "No account signed in or interaction required");
        }
        return Task.FromResult<string?>("silent_test_token");
    }

    public Task<string?> AcquireTokenInteractiveAsync(string[] scopes, CancellationToken cancellationToken = default)
    {
        InteractiveCallCount++;
        HasAccount = true;
        SelectedHomeAccountId ??= "test-account";
        return Task.FromResult(InteractiveTokenToReturn);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOutCallCount++;
        HasAccount = false;
        SelectedHomeAccountId = null;
        return Task.CompletedTask;
    }

    public Task<bool> HasAccountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HasAccount && !string.IsNullOrEmpty(SelectedHomeAccountId));
    }
}
