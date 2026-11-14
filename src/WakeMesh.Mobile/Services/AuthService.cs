using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public sealed class AuthService : IAuthService, IAuthSessionLifecycle
{
    private readonly IAuthSettingsStore _settingsStore;
    private readonly IMsalClientAdapter _msalAdapter;
    private readonly HttpClient _httpClient;
    private readonly IDeviceCredentialStore? _credentialStore;
    private readonly IDeviceInfoProvider? _deviceInfoProvider;
    private readonly string _compileTimeClientId;
    private readonly object _initSync = new();
    private Task? _initializeTask;
    private volatile bool _initialized;

    private AuthState _state = AuthState.NotConfigured;
    private string _statusMessage = string.Empty;
    private AuthSettingsLoadDiagnostic? _settingsLoadDiagnostic;

    public AuthState State => _state;
    public string StatusMessage => _statusMessage;
    public AuthSettings Settings { get; private set; }
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Typed diagnostic for the persisted auth settings. <see cref="AuthSettingsLoadStatus.Corrupted"/>
    /// means the store could not be trusted: the state is NOT reported as a plain unconfigured state.
    /// </summary>
    public AuthSettingsLoadDiagnostic? SettingsLoadDiagnostic => _settingsLoadDiagnostic;

    public AccountBinding CurrentBinding => Settings.ToAccountBinding(_msalAdapter.SelectedHomeAccountId);

    public bool CanSignIn => (_state == AuthState.SignedOut || _state == AuthState.ReauthRequired) &&
                             Settings.Validate().IsValid &&
                             Settings.Validate().IsConfigured &&
                             Settings.MatchesCompileTimeClientId(_compileTimeClientId);

    public bool CanSignOut => true; // SignOut is always permissible, even after clearing settings

    public event EventHandler? StateChanged;
    public event EventHandler<AuthSessionEndedEventArgs>? SessionEnded;

    public AuthService(
        IAuthSettingsStore settingsStore,
        IMsalClientAdapter msalAdapter,
        AuthSettings? initialSettings = null,
        HttpClient? httpClient = null,
        IDeviceCredentialStore? credentialStore = null,
        IDeviceInfoProvider? deviceInfoProvider = null,
        string? compileTimeClientId = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _msalAdapter = msalAdapter ?? throw new ArgumentNullException(nameof(msalAdapter));
        _httpClient = httpClient ?? new HttpClient();
        _credentialStore = credentialStore;
        _deviceInfoProvider = deviceInfoProvider;
        _compileTimeClientId = compileTimeClientId ?? CompiledAuthConstants.CompileTimeClientId;

        if (initialSettings != null)
        {
            // Explicit seed (tests / callers): treat as already-loaded settings snapshot.
            Settings = initialSettings;
            _msalAdapter.UpdateClientId(Settings.AndroidClientId);
            EvaluateState(accountProbe: false);
            _initialized = true;
        }
        else
        {
            // Never block the UI thread and never pretend a pending load is "empty settings".
            // Only accept a synchronously completed load; otherwise keep a placeholder until InitializeAsync.
            var loadTask = _settingsStore.LoadSettingsAsync();
            if (loadTask.IsCompletedSuccessfully)
            {
                Settings = loadTask.Result ?? new AuthSettings();
                _msalAdapter.UpdateClientId(Settings.AndroidClientId);
                ApplyPersistedHomeAccountIdFromStore();
                EvaluateState(accountProbe: false);
                // Still require InitializeAsync for account cache probe / idempotent gate completion.
                _initialized = false;
            }
            else
            {
                Settings = new AuthSettings();
                if (loadTask.IsFaulted)
                {
                    // Observe the fault (no unobserved task exception) and keep a typed warning.
                    var fault = loadTask.Exception?.GetBaseException();
                    _settingsLoadDiagnostic = ToDiagnostic(fault);
                    _ = loadTask.Exception;
                }

                _initialized = false;
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return EnsureInitializedAsync(cancellationToken);
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        Task initTask;
        lock (_initSync)
        {
            if (_initialized)
            {
                return Task.CompletedTask;
            }

            _initializeTask ??= InitializeCoreAsync();
            initTask = _initializeTask;
        }

        if (cancellationToken.CanBeCanceled)
        {
            return initTask.WaitAsync(cancellationToken);
        }

        return initTask;
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await ReloadSettingsAsync(force: true, cancellationToken: default);
        }
        finally
        {
            _initialized = true;
        }
    }

    public async Task ReloadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ReloadSettingsAsync(force: true, cancellationToken);
    }

    private async Task ReloadSettingsAsync(bool force, CancellationToken cancellationToken)
    {
        _ = force;
        var previousBinding = CurrentBinding;
        var previousOrigin = previousBinding.ApiOrigin;
        var previousApiClientId = previousBinding.ApiClientId;
        var previousAndroidClientId = previousBinding.AndroidClientId;

        AuthSettings loaded;
        try
        {
            loaded = await _settingsStore.LoadSettingsAsync(cancellationToken) ?? new AuthSettings();
            _settingsLoadDiagnostic = LoadDiagnosticFromStore();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loaded = new AuthSettings();
            _settingsLoadDiagnostic = ToDiagnostic(ex);
        }

        var newOrigin = AccountBinding.NormalizeApiOrigin(loaded.ApiBaseUrl);
        var newApiClientId = AccountBinding.NormalizeGuid(loaded.ApiClientId) ?? string.Empty;
        var newAndroidClientId = AccountBinding.NormalizeGuid(loaded.AndroidClientId) ?? string.Empty;

        // Only treat as an identity change when we already had a configured binding.
        // First InitializeAsync from constructor placeholder → disk must NOT clear persisted home account.
        var previousConfigured =
            !string.IsNullOrEmpty(previousOrigin) ||
            !string.IsNullOrEmpty(previousApiClientId) ||
            !string.IsNullOrEmpty(previousAndroidClientId);

        var identityChanged = previousConfigured &&
            (!string.Equals(previousOrigin, newOrigin, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(previousApiClientId, newApiClientId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(previousAndroidClientId, newAndroidClientId, StringComparison.OrdinalIgnoreCase));

        // Capture old binding secrets before mutating auth selection / settings.
        if (identityChanged)
        {
            await DeleteCredentialForBindingAsync(previousBinding, cancellationToken);
            await SignOutMsalPreservingEvaluationAsync(cancellationToken);
            await PersistHomeAccountIdAsync(null, cancellationToken);
        }

        Settings = loaded;
        _msalAdapter.UpdateClientId(Settings.AndroidClientId);

        if (identityChanged)
        {
            _msalAdapter.SetSelectedHomeAccountId(null);
        }
        else
        {
            ApplyPersistedHomeAccountIdFromStore();
        }

        await EvaluateStateAsync(cancellationToken);
    }

    private void ApplyPersistedHomeAccountIdFromStore()
    {
        if (_settingsStore is JsonAuthSettingsStore jsonStore)
        {
            var homeId = jsonStore.SelectedHomeAccountId;
            if (!string.IsNullOrWhiteSpace(homeId))
            {
                _msalAdapter.SetSelectedHomeAccountId(homeId);
            }
        }
    }

    private async Task PersistHomeAccountIdAsync(string? homeAccountId, CancellationToken cancellationToken)
    {
        if (_settingsStore is JsonAuthSettingsStore jsonStore)
        {
            await jsonStore.SaveSelectedHomeAccountIdAsync(homeAccountId, cancellationToken);
        }

        _msalAdapter.SetSelectedHomeAccountId(homeAccountId);
    }

    private async Task DeleteCredentialForBindingAsync(AccountBinding binding, CancellationToken cancellationToken)
    {
        if (_credentialStore == null)
        {
            return;
        }

        try
        {
            if (_credentialStore is SecureDeviceCredentialStore secureStore)
            {
                // Purge every server device id registered under this binding (the credential key is the
                // server device id, never the local device id).
                await secureStore.ClearCredentialsForBindingAsync(binding, cancellationToken);

                // Legacy safety net: older builds keyed credentials by the local device id.
                if (_deviceInfoProvider != null && Guid.TryParse(_deviceInfoProvider.DeviceId, out var localDeviceId))
                {
                    await secureStore.DeleteCredentialAsync(localDeviceId, binding, cancellationToken);
                }
            }
            else if (_deviceInfoProvider != null && Guid.TryParse(_deviceInfoProvider.DeviceId, out var deviceId))
            {
                await _credentialStore.DeleteCredentialAsync(deviceId, cancellationToken);
            }
        }
        catch
        {
            // Credential cleanup must never block sign-out; the failure only means no further secrets
            // can be read for the old binding (the binding keys no longer match the new settings).
        }
    }

    private AuthSettingsLoadDiagnostic? LoadDiagnosticFromStore()
        => _settingsStore is JsonAuthSettingsStore jsonStore ? jsonStore.LastLoadDiagnostic : null;

    private static AuthSettingsLoadDiagnostic ToDiagnostic(Exception? exception)
        => exception switch
        {
            CorruptedAuthSettingsException corrupted => new AuthSettingsLoadDiagnostic(
                AuthSettingsLoadStatus.Corrupted,
                corrupted.Code,
                corrupted.FileName),
            null => new AuthSettingsLoadDiagnostic(AuthSettingsLoadStatus.Corrupted, "loadFailed"),
            _ => new AuthSettingsLoadDiagnostic(
                AuthSettingsLoadStatus.Corrupted,
                "loadFailed",
                exception.GetType().Name),
        };

    private async Task SignOutMsalPreservingEvaluationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _msalAdapter.SignOutAsync(cancellationToken);
        }
        catch
        {
            // Ignore signout error
        }
    }

    private void EvaluateState(bool accountProbe)
    {
        _ = accountProbe;

        // A corrupt/unreadable settings document is a typed warning state — never a silent "未設定".
        if (_settingsLoadDiagnostic is { HasWarning: true } settingsWarning)
        {
            _state = AuthState.NotConfigured;
            _statusMessage =
                $"認証設定を読み込めませんでした (code={settingsWarning.Code})。設定ファイルが破損している可能性があるため、" +
                "未設定として黙って扱いません。内容を確認してから保存し直してください。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var validation = Settings.Validate();
        if (!validation.IsConfigured)
        {
            _state = AuthState.NotConfigured;
            _statusMessage = "未設定: Microsoft Entra ID / API 設定が未入力です (ローカル単独運用中)";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!validation.IsValid)
        {
            _state = AuthState.NotConfigured;
            _statusMessage = $"設定が無効です: {validation.ErrorMessage}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!Settings.MatchesCompileTimeClientId(_compileTimeClientId))
        {
            _state = AuthState.NeedsRebuild;
            _statusMessage = $"ビルド時 WakeMeshAuthClientId ({_compileTimeClientId}) と入力された ClientId ({Settings.AndroidClientId}) が一致しません。Redirect URI (msal{_compileTimeClientId}://auth) 安全処理のため、-p:WakeMeshAuthClientId={Settings.AndroidClientId} を指定して再ビルドしてください。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _state = AuthState.SignedOut;
        _statusMessage = "サインアウト済み";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EvaluateStateAsync(CancellationToken cancellationToken)
    {
        EvaluateState(accountProbe: false);

        if (_state is not (AuthState.SignedOut or AuthState.ReauthRequired))
        {
            return;
        }

        var selectedId = _msalAdapter.SelectedHomeAccountId;
        if (string.IsNullOrEmpty(selectedId))
        {
            return;
        }

        try
        {
            if (await _msalAdapter.HasAccountAsync(cancellationToken))
            {
                _state = AuthState.SignedIn;
                _statusMessage = "サインイン済み";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Persisted binding exists but MSAL cache does not — honest reauth, no FirstOrDefault.
                _state = AuthState.ReauthRequired;
                _statusMessage = "再認証が必要です。手動でサインインしてください。";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            _state = AuthState.ReauthRequired;
            _statusMessage = "再認証が必要です。手動でサインインしてください。";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (_state == AuthState.NotConfigured || _state == AuthState.NeedsRebuild)
        {
            return null;
        }

        var scope = Settings.EffectiveScope;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        try
        {
            var scopes = new[] { scope };
            var token = await _msalAdapter.AcquireTokenSilentAsync(scopes, cancellationToken);
            if (!string.IsNullOrEmpty(token))
            {
                if (_state != AuthState.SignedIn)
                {
                    _state = AuthState.SignedIn;
                    _statusMessage = "サインイン済み";
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                return token;
            }

            return null;
        }
        catch (MsalUiRequiredException)
        {
            // Background sync calls silent; on UI required, typed re-login waiting state is recorded.
            // NEVER trigger interactive authentication in the background!
            // No selected account means no session to renew, including after a process restart.
            // Keep SignedOut instead of turning a signed-out session into a reauthentication request.
            if (string.IsNullOrEmpty(_msalAdapter.SelectedHomeAccountId))
            {
                return null;
            }

            _state = AuthState.ReauthRequired;
            _statusMessage = "再認証が必要です。手動でサインインしてください。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> SignInInteractiveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (_state == AuthState.NeedsRebuild || _state == AuthState.NotConfigured)
        {
            return false;
        }

        var scope = Settings.EffectiveScope;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        var oldBinding = CurrentBinding;

        _state = AuthState.SigningIn;
        _statusMessage = "サインイン処理中...";
        StateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var scopes = new[] { scope };
            var token = await _msalAdapter.AcquireTokenInteractiveAsync(scopes, cancellationToken);
            if (!string.IsNullOrEmpty(token))
            {
                var newHomeId = _msalAdapter.SelectedHomeAccountId;
                if (!string.Equals(oldBinding.HomeAccountId, newHomeId, StringComparison.Ordinal))
                {
                    // Account switched: remove secrets bound to the previous account before publishing the new binding.
                    await DeleteCredentialForBindingAsync(oldBinding, cancellationToken);
                }

                await PersistHomeAccountIdAsync(newHomeId, cancellationToken);

                if (!string.Equals(oldBinding.HomeAccountId, newHomeId, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(oldBinding.HomeAccountId))
                {
                    RaiseSessionEnded(oldBinding, AuthSessionEndReason.IdentityChanged);
                }

                _state = AuthState.SignedIn;
                _statusMessage = "サインイン完了";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            _state = AuthState.SignedOut;
            _statusMessage = "サインインが完了しませんでした。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            _state = AuthState.SignedOut;
            _statusMessage = "サインインがキャンセルされました。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        catch (Exception ex)
        {
            _state = AuthState.SignedOut;
            _statusMessage = $"サインインに失敗しました: {ex.Message}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // Sign-out must work even when settings were cleared / init partially failed.
        try
        {
            await EnsureInitializedAsync(cancellationToken);
        }
        catch
        {
            // Continue with best-effort sign-out
        }

        var oldBinding = CurrentBinding;

        // Remove binding-scoped secrets BEFORE clearing MSAL selection.
        await DeleteCredentialForBindingAsync(oldBinding, cancellationToken);

        try
        {
            await _msalAdapter.SignOutAsync(cancellationToken);
        }
        catch
        {
            // Ignore signout error
        }

        try
        {
            await PersistHomeAccountIdAsync(null, cancellationToken);
        }
        catch
        {
            _msalAdapter.SetSelectedHomeAccountId(null);
        }

        // Local alarms are strictly preserved - never deleted or disabled on signout!

        EvaluateState(accountProbe: false);
        if (_state == AuthState.SignedIn || _state == AuthState.ReauthRequired)
        {
            _state = AuthState.SignedOut;
            _statusMessage = "サインアウトしました。";
        }
        else if (_state == AuthState.SignedOut)
        {
            _statusMessage = "サインアウトしました。";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        RaiseSessionEnded(oldBinding, AuthSessionEndReason.ExplicitSignOut);
    }

    public async Task ApplySettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureInitializedAsync(cancellationToken);

        var previousBinding = CurrentBinding;
        var newOrigin = AccountBinding.NormalizeApiOrigin(settings.ApiBaseUrl);
        var newApiClientId = AccountBinding.NormalizeGuid(settings.ApiClientId) ?? string.Empty;
        var newAndroidClientId = AccountBinding.NormalizeGuid(settings.AndroidClientId) ?? string.Empty;

        var previousConfigured =
            !string.IsNullOrEmpty(previousBinding.ApiOrigin) ||
            !string.IsNullOrEmpty(previousBinding.ApiClientId) ||
            !string.IsNullOrEmpty(previousBinding.AndroidClientId);

        var identityChanged = previousConfigured &&
            (!string.Equals(previousBinding.ApiOrigin, newOrigin, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(previousBinding.ApiClientId, newApiClientId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(previousBinding.AndroidClientId, newAndroidClientId, StringComparison.OrdinalIgnoreCase));

        if (identityChanged)
        {
            await DeleteCredentialForBindingAsync(previousBinding, cancellationToken);
            await SignOutMsalPreservingEvaluationAsync(cancellationToken);
            await PersistHomeAccountIdAsync(null, cancellationToken);
        }

        await _settingsStore.SaveSettingsAsync(settings, cancellationToken);
        Settings = settings;
        // The user just wrote a fresh, authoritative document: the previous corruption warning is resolved.
        _settingsLoadDiagnostic = null;
        _msalAdapter.UpdateClientId(Settings.AndroidClientId);

        if (identityChanged)
        {
            _msalAdapter.SetSelectedHomeAccountId(null);
        }
        else
        {
            ApplyPersistedHomeAccountIdFromStore();
        }

        await EvaluateStateAsync(cancellationToken);

        if (identityChanged)
        {
            RaiseSessionEnded(previousBinding, AuthSessionEndReason.IdentityChanged);
        }
    }

    public async Task ClearSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var oldBinding = CurrentBinding;
        await DeleteCredentialForBindingAsync(oldBinding, cancellationToken);
        await SignOutMsalPreservingEvaluationAsync(cancellationToken);
        await PersistHomeAccountIdAsync(null, cancellationToken);

        var empty = new AuthSettings();
        await _settingsStore.SaveSettingsAsync(empty, cancellationToken);
        Settings = empty;
        _settingsLoadDiagnostic = null;
        _msalAdapter.UpdateClientId(null);
        _msalAdapter.SetSelectedHomeAccountId(null);

        EvaluateState(accountProbe: false);
        RaiseSessionEnded(oldBinding, AuthSessionEndReason.SettingsCleared);
    }

    private void RaiseSessionEnded(AccountBinding oldBinding, AuthSessionEndReason reason)
    {
        // Snapshot is already an immutable record; copy constructor keeps event args stable if callers retain it.
        var snapshot = oldBinding with { };
        SessionEnded?.Invoke(this, new AuthSessionEndedEventArgs(snapshot, reason));
    }

    public async Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var validation = Settings.Validate();
        if (!validation.IsConfigured || string.IsNullOrWhiteSpace(Settings.ApiBaseUrl))
        {
            return ConnectionCheckResult.Unconfigured("API設定が未設定です。ローカル単独で動作しています。");
        }

        if (!validation.IsValid)
        {
            return ConnectionCheckResult.Failed($"設定が無効です: {validation.ErrorMessage}");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var uri = new Uri(Settings.ApiBaseUrl);
            var response = await _httpClient.GetAsync(uri, cts.Token);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return ConnectionCheckResult.Success($"接続確認成功 (HTTP {code})", code);
            }
            return ConnectionCheckResult.Failed($"サーバー応答あり (HTTP {code})", code);
        }
        catch (Exception ex)
        {
            return ConnectionCheckResult.Failed($"接続失敗: {ex.Message}");
        }
    }
}
