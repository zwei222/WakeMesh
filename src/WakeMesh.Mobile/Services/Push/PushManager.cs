using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services.CloudManagement;

namespace WakeMesh.Mobile.Services.Push;

public sealed class PushManager : IPushManager, IDisposable
{
    public const string ServerDeletionUnconfirmed = "ServerDeletionUnconfirmed";

    private readonly IAuthService _authService;
    private readonly IAuthSessionLifecycle? _authSessionLifecycle;
    private readonly ICloudDeviceRegistrationStore _registrationStore;
    private readonly ICloudManagementService? _cloudManagementService;
    private readonly IDeviceCredentialStore? _credentialStore;
    private readonly IPushTokenStore _tokenStore;
    private readonly IPushNotificationAdapter _notificationAdapter;
    private readonly IWorkManagerAdapter _workManagerAdapter;
    private readonly Func<string, IPushApiClient>? _clientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// Monotonic generation bumped whenever a token is saved under a binding scope.
    /// Session-end cleanup captures the generation at event time and only clears when the
    /// durable token still belongs to the ended binding (never a newer scope registration).
    /// </summary>
    private int _tokenGeneration;

    private PushServiceStatus _status = PushServiceStatus.NotConfigured;
    private string? _currentToken;
    private string? _currentBindingKey;
    private string? _lastRegisteredToken;
    private bool? _lastSentNotificationsCapable;
    private string? _lastError;
    private bool _isConfigured;
    private bool _isDisposed;

    public PushServiceStatus Status => _status;
    public string? CurrentToken => _currentToken;
    public string? LastRegisteredToken => _lastRegisteredToken;
    public bool? LastSentNotificationsCapable => _lastSentNotificationsCapable;
    public string? LastError => _lastError;
    public bool IsConfigured => _isConfigured;

    public event EventHandler? StatusChanged;

    public PushManager(
        IAuthService authService,
        ICloudDeviceRegistrationStore registrationStore,
        IDeviceCredentialStore? credentialStore,
        IPushTokenStore tokenStore,
        IPushNotificationAdapter notificationAdapter,
        IWorkManagerAdapter workManagerAdapter,
        bool isConfigured = true,
        Func<string, IPushApiClient>? clientFactory = null,
        ICloudManagementService? cloudManagementService = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _authSessionLifecycle = authService as IAuthSessionLifecycle;
        _registrationStore = registrationStore ?? throw new ArgumentNullException(nameof(registrationStore));
        _cloudManagementService = cloudManagementService;
        _credentialStore = credentialStore;
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _notificationAdapter = notificationAdapter ?? throw new ArgumentNullException(nameof(notificationAdapter));
        _workManagerAdapter = workManagerAdapter ?? throw new ArgumentNullException(nameof(workManagerAdapter));
        _isConfigured = isConfigured;
        _clientFactory = clientFactory;

        if (!_isConfigured)
        {
            _status = PushServiceStatus.NotConfigured;
        }
        else
        {
            _status = PushServiceStatus.Idle;
        }

        // StateChanged is only a re-registration hint after a definitive SignedIn.
        // Destructive cleanup is exclusively SessionEnded (when the runtime auth implements it).
        _authService.StateChanged += OnAuthStateChanged;
        if (_authSessionLifecycle != null)
        {
            _authSessionLifecycle.SessionEnded += OnAuthSessionEnded;
        }

        if (_cloudManagementService != null)
        {
            _cloudManagementService.RegistrationChanged += OnRegistrationChanged;
        }

        _notificationAdapter.NotificationPermissionChanged += OnNotificationPermissionChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
        {
            SetStatus(PushServiceStatus.NotConfigured, "Push feature not configured (missing google-services.json or Firebase unavailable)");
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadTokenStateLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(_currentToken))
            {
                await TryRegisterCurrentTokenLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SetStatus(PushServiceStatus.Idle);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnNewTokenReceivedAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        if (!_isConfigured)
        {
            SetStatus(PushServiceStatus.NotConfigured, "Push feature not configured");
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistTokenLockedAsync(token, cancellationToken).ConfigureAwait(false);
            await TryRegisterCurrentTokenLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PushPayloadValidationResult> OnPushMessageReceivedAsync(
        IReadOnlyDictionary<string, string>? data,
        CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.MissingData, "NotConfigured");
        }

        // Restore cold settings and account binding prior to message scope validation without unbounded network wait
        await _authService.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var registration = await _registrationStore.LoadRegistrationAsync(cancellationToken).ConfigureAwait(false);
        var currentUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
        var currentAccount = _authService.CurrentBinding?.HomeAccountId ?? string.Empty;

        var validation = PushPayloadValidator.Validate(data, registration, currentUrl, currentAccount);
        if (!validation.IsValid)
        {
            return validation;
        }

        // Show immediate user-visible notification if permission is present
        if (_notificationAdapter.HasNotificationPermission)
        {
            _notificationAdapter.ShowSettingsChangedNotification();
        }

        // Enqueue background sync via WorkManager (never await HTTP inside receiver)
        _workManagerAdapter.EnqueuePushSyncWork();

        return validation;
    }

    public async Task<bool> UnregisterTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UnregisterTokenLockedAsync(
                requireValidSessionForServerDelete: true,
                endedBinding: null,
                capturedGeneration: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadTokenStateLockedAsync(CancellationToken cancellationToken)
    {
        _currentToken = await _tokenStore.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        _currentBindingKey = await _tokenStore.GetBindingKeyAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_currentToken) && string.IsNullOrEmpty(_currentBindingKey))
        {
            // Legacy durable rows lack binding metadata: stamp the current scope when known so a
            // later SessionEnded cannot mix old and new bindings.
            var key = TryCurrentBindingKey();
            if (!string.IsNullOrEmpty(key))
            {
                await _tokenStore.SaveTokenWithBindingAsync(_currentToken, key, cancellationToken).ConfigureAwait(false);
                _currentBindingKey = key;
                Interlocked.Increment(ref _tokenGeneration);
            }
        }
    }

    private async Task PersistTokenLockedAsync(string token, CancellationToken cancellationToken)
    {
        var key = TryCurrentBindingKey();
        if (!string.IsNullOrEmpty(key))
        {
            await _tokenStore.SaveTokenWithBindingAsync(token, key, cancellationToken).ConfigureAwait(false);
            _currentBindingKey = key;
        }
        else
        {
            await _tokenStore.SaveTokenAsync(token, cancellationToken).ConfigureAwait(false);
            _currentBindingKey = await _tokenStore.GetBindingKeyAsync(cancellationToken).ConfigureAwait(false);
        }

        _currentToken = token;
        Interlocked.Increment(ref _tokenGeneration);
    }

    private string? TryCurrentBindingKey()
    {
        try
        {
            var binding = _authService.CurrentBinding;
            if (binding == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(binding.ApiOrigin) &&
                string.IsNullOrEmpty(binding.ApiClientId) &&
                string.IsNullOrEmpty(binding.AndroidClientId) &&
                string.IsNullOrEmpty(binding.HomeAccountId))
            {
                return null;
            }

            return binding.GetBindingKey();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> UnregisterTokenLockedAsync(
        bool requireValidSessionForServerDelete,
        AccountBinding? endedBinding,
        int? capturedGeneration,
        CancellationToken cancellationToken)
    {
        SetStatus(PushServiceStatus.Unregistering);

        if (endedBinding != null)
        {
            // Only clear tokens that still belong to the ended scope / generation.
            var endedKey = endedBinding.GetBindingKey();
            var durableToken = await _tokenStore.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            var durableKey = await _tokenStore.GetBindingKeyAsync(cancellationToken).ConfigureAwait(false);

            if (capturedGeneration is int gen && Volatile.Read(ref _tokenGeneration) != gen)
            {
                // A newer token was persisted after the event was raised — leave it alone.
                SetStatus(PushServiceStatus.Idle);
                return false;
            }

            var currentMatches =
                !string.IsNullOrEmpty(_currentToken) &&
                (string.IsNullOrEmpty(_currentBindingKey) ||
                 string.Equals(_currentBindingKey, endedKey, StringComparison.Ordinal));

            var durableMatches =
                !string.IsNullOrEmpty(durableToken) &&
                (string.IsNullOrEmpty(durableKey) ||
                 string.Equals(durableKey, endedKey, StringComparison.Ordinal));

            if (!currentMatches && !durableMatches)
            {
                // Token already belongs to another scope.
                SetStatus(string.IsNullOrEmpty(_currentToken) ? PushServiceStatus.Idle : _status == PushServiceStatus.Unregistering ? PushServiceStatus.Idle : _status);
                if (!string.IsNullOrEmpty(_currentToken) && _authService.State == AuthState.SignedIn)
                {
                    SetStatus(PushServiceStatus.Registered);
                }
                else if (!string.IsNullOrEmpty(_currentToken))
                {
                    SetStatus(PushServiceStatus.PendingAuthOrRegistration);
                }
                else
                {
                    SetStatus(PushServiceStatus.Idle);
                }

                return false;
            }

            // Session already ended: never attempt a server DELETE under a dead session, and never
            // claim the server copy was removed. Clear matching local state and report honestly.
            await ClearLocalTokenAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(PushServiceStatus.UnregistrationFailed, ServerDeletionUnconfirmed);
            return false;
        }

        var registration = await _registrationStore.LoadRegistrationAsync(cancellationToken).ConfigureAwait(false);
        if (registration == null || registration.RecoveryNeeded)
        {
            await ClearLocalTokenAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(PushServiceStatus.Unregistered);
            return true;
        }

        var currentUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentUrl))
        {
            await ClearLocalTokenAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(PushServiceStatus.Unregistered);
            return true;
        }

        // Public unregister: server DELETE only while a usable SignedIn session still exists.
        var sessionUsable = _authService.State == AuthState.SignedIn;
        if (requireValidSessionForServerDelete && !sessionUsable)
        {
            await ClearLocalTokenAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(PushServiceStatus.UnregistrationFailed, ServerDeletionUnconfirmed);
            return false;
        }

        if (!sessionUsable)
        {
            await ClearLocalTokenAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(PushServiceStatus.UnregistrationFailed, ServerDeletionUnconfirmed);
            return false;
        }

        try
        {
            var client = GetClient(currentUrl);
            var deleteResult = await client.DeletePushTokenAsync(registration.ServerDeviceId, cancellationToken).ConfigureAwait(false);
            if (deleteResult.IsSuccess)
            {
                await ClearLocalTokenAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(PushServiceStatus.Unregistered);
                return true;
            }

            // Session still valid: keep the local token so a later retry can revoke it.
            SetStatus(PushServiceStatus.UnregistrationFailed, deleteResult.Message ?? deleteResult.Kind.ToString());
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(PushServiceStatus.UnregistrationFailed, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(PushServiceStatus.UnregistrationFailed, ex.GetType().Name);
            return false;
        }
    }

    private async Task ClearLocalTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenStore.ClearTokenAsync(cancellationToken).ConfigureAwait(false);
        _currentToken = null;
        _currentBindingKey = null;
        _lastRegisteredToken = null;
        _lastSentNotificationsCapable = null;
        Interlocked.Increment(ref _tokenGeneration);
    }

    private void OnRegistrationChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isConfigured) return;

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrEmpty(_currentToken))
                {
                    await TryRegisterCurrentTokenLockedAsync().ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void OnNotificationPermissionChanged(object? sender, EventArgs e)
    {
        _ = OnNotificationPermissionChangedAsync();
    }

    public async Task OnNotificationPermissionChangedAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !_isConfigured) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_currentToken))
            {
                bool perm = _notificationAdapter.HasNotificationPermission;
                if (_lastSentNotificationsCapable != perm ||
                    string.IsNullOrEmpty(_lastRegisteredToken) ||
                    !string.Equals(_currentToken, _lastRegisteredToken, StringComparison.Ordinal))
                {
                    await TryRegisterCurrentTokenLockedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnAuthSessionEnded(object? sender, AuthSessionEndedEventArgs e)
    {
        if (_isDisposed || !_isConfigured || e == null)
        {
            return;
        }

        var oldBinding = e.OldBinding;
        var generation = Volatile.Read(ref _tokenGeneration);
        var lifetimeToken = _lifetimeCts.Token;

        _ = Task.Run(async () =>
        {
            if (_isDisposed || lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _gate.WaitAsync(lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (_isDisposed || lifetimeToken.IsCancellationRequested)
                {
                    return;
                }

                await UnregisterTokenLockedAsync(
                    requireValidSessionForServerDelete: false,
                    endedBinding: oldBinding,
                    capturedGeneration: generation,
                    lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            finally
            {
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        }, CancellationToken.None);
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isConfigured)
        {
            return;
        }

        // Never destroy tokens from StateChanged. After initialize completes, a definitive SignedIn
        // may re-register; every other state keeps the local token untouched.
        var lifetimeToken = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _authService.InitializeAsync(lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }

            if (_isDisposed || lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            if (_authService.State != AuthState.SignedIn)
            {
                return;
            }

            await RegisterCurrentTokenIfNeededAsync(lifetimeToken).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    private async Task RegisterCurrentTokenIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed || string.IsNullOrEmpty(_currentToken))
        {
            // Token may only live in the durable store (cold start before InitializeAsync completed).
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                if (string.IsNullOrEmpty(_currentToken))
                {
                    await LoadTokenStateLockedAsync(cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(_currentToken))
                {
                    return;
                }

                await TryRegisterCurrentTokenLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isDisposed)
            {
                return;
            }

            bool perm = _notificationAdapter.HasNotificationPermission;
            if (string.IsNullOrEmpty(_lastRegisteredToken) ||
                !string.Equals(_currentToken, _lastRegisteredToken, StringComparison.Ordinal) ||
                _lastSentNotificationsCapable != perm)
            {
                await TryRegisterCurrentTokenLockedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TryRegisterCurrentTokenLockedAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_currentToken))
        {
            SetStatus(PushServiceStatus.Idle);
            return;
        }

        // Ensure binding metadata exists before a later SessionEnded has to decide scope membership.
        if (string.IsNullOrEmpty(_currentBindingKey))
        {
            var key = TryCurrentBindingKey();
            if (!string.IsNullOrEmpty(key))
            {
                await _tokenStore.SaveTokenWithBindingAsync(_currentToken, key, cancellationToken).ConfigureAwait(false);
                _currentBindingKey = key;
                Interlocked.Increment(ref _tokenGeneration);
            }
        }

        if (_authService.State != AuthState.SignedIn)
        {
            SetStatus(PushServiceStatus.PendingAuthOrRegistration);
            return;
        }

        var registration = await _registrationStore.LoadRegistrationAsync(cancellationToken).ConfigureAwait(false);
        if (registration == null || registration.RecoveryNeeded)
        {
            SetStatus(PushServiceStatus.PendingAuthOrRegistration);
            return;
        }

        var currentUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
        var currentAccount = _authService.CurrentBinding?.HomeAccountId ?? string.Empty;
        if (!registration.MatchesScope(currentUrl, currentAccount))
        {
            SetStatus(PushServiceStatus.PendingAuthOrRegistration);
            return;
        }

        try
        {
            bool notificationsCapable = _notificationAdapter.HasNotificationPermission;
            var client = GetClient(currentUrl);
            var putResult = await client.PutPushTokenAsync(registration.ServerDeviceId, _currentToken, notificationsCapable, cancellationToken).ConfigureAwait(false);

            if (putResult.IsSuccess)
            {
                _lastRegisteredToken = _currentToken;
                _lastSentNotificationsCapable = notificationsCapable;
                SetStatus(PushServiceStatus.Registered);
            }
            else
            {
                SetStatus(PushServiceStatus.RegistrationFailed, putResult.Message ?? putResult.Kind.ToString());
            }
        }
        catch (Exception ex)
        {
            SetStatus(PushServiceStatus.RegistrationFailed, ex.GetType().Name);
        }
    }

    private IPushApiClient GetClient(string apiUrl)
    {
        if (_clientFactory != null)
        {
            return _clientFactory(apiUrl);
        }

        return new DefaultPushApiClient(WakeMeshApiClient.Create(apiUrl, _authService, _credentialStore));
    }

    private void SetStatus(PushServiceStatus status, string? error = null)
    {
        _status = status;
        _lastError = error;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _authService.StateChanged -= OnAuthStateChanged;
        if (_authSessionLifecycle != null)
        {
            _authSessionLifecycle.SessionEnded -= OnAuthSessionEnded;
        }

        if (_cloudManagementService != null)
        {
            _cloudManagementService.RegistrationChanged -= OnRegistrationChanged;
        }

        _notificationAdapter.NotificationPermissionChanged -= OnNotificationPermissionChanged;

        _lifetimeCts.Cancel();
        _gate.Dispose();
    }
}
