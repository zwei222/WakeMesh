using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.Services.CloudManagement;

public sealed class CloudManagementService : ICloudManagementService, IDisposable
{
    private readonly IAuthService _authService;
    private readonly ICloudDeviceRegistrationStore _registrationStore;
    private readonly IDeviceCredentialStore? _credentialStore;
    private readonly AlarmCoordinator _alarmCoordinator;
    private readonly Func<WakeMeshApiClient>? _clientFactory;
    private readonly string? _customStorageDir;
    private readonly DesiredObservationStore? _observations;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _scopeGate = new(1, 1);
    private CancellationTokenSource _inFlightCts = new();
    private WakeMeshApiClient? _cachedClient;
    private string? _cachedApiBaseUrl;
    private long _observationEpoch;

    public event EventHandler? RegistrationChanged;
    public event EventHandler? SyncCompleted;

    public bool IsConfiguredAndSignedIn =>
        _authService.State == AuthState.SignedIn &&
        !string.IsNullOrWhiteSpace(_authService.Settings.ApiBaseUrl);

    public string? CurrentUserAccountId
    {
        get
        {
            var homeId = _authService.CurrentBinding.HomeAccountId;
            if (!string.IsNullOrWhiteSpace(homeId))
            {
                return homeId;
            }

            var key = _authService.CurrentBinding.GetBindingKey();
            return string.IsNullOrWhiteSpace(key) ? "local-user" : key;
        }
    }

    public CloudManagementService(
        IAuthService authService,
        ICloudDeviceRegistrationStore registrationStore,
        IDeviceCredentialStore? credentialStore,
        AlarmCoordinator alarmCoordinator,
        Func<WakeMeshApiClient>? clientFactory = null,
        string? customStorageDir = null,
        DesiredObservationStore? observations = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _registrationStore = registrationStore ?? throw new ArgumentNullException(nameof(registrationStore));
        _credentialStore = credentialStore;
        _alarmCoordinator = alarmCoordinator ?? throw new ArgumentNullException(nameof(alarmCoordinator));
        _clientFactory = clientFactory;
        _customStorageDir = customStorageDir;
        _observations = observations;

        _authService.StateChanged += OnAuthStateChanged;
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            // T118: the last observation belongs to the previous identity. Forget it and make sure an
            // in-flight cycle cannot publish its result as if it belonged to the new identity.
            InvalidateObservationScope();

            // If settings change or sign-out happens, cancel active calls immediately to prevent sending secrets/ACK
            try
            {
                _inFlightCts.Cancel();
                _inFlightCts.Dispose();
            }
            catch
            {
                // Ignore disposal errors on cancel
            }
            _inFlightCts = new CancellationTokenSource();

            if (_cachedClient != null)
            {
                // Only dispose if we created it (not an external test injection that owns itself)
                if (_clientFactory == null)
                {
                    _cachedClient.Dispose();
                }
                _cachedClient = null;
                _cachedApiBaseUrl = null;
            }
        }
    }

    private WakeMeshApiClient GetClient()
    {
        if (_clientFactory != null)
        {
            return _clientFactory();
        }

        lock (_lock)
        {
            var currentUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
            if (_cachedClient != null && string.Equals(_cachedApiBaseUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedClient;
            }

            if (_cachedClient != null)
            {
                _cachedClient.Dispose();
                _cachedClient = null;
            }

            _cachedClient = WakeMeshApiClient.Create(currentUrl, _authService, _credentialStore);
            _cachedApiBaseUrl = currentUrl;
            return _cachedClient;
        }
    }

    private WakeMeshApiClient CreateRegistrationClient(
        AccountBinding startingBinding,
        string startingUrl,
        ScopedRegistrationCredentialStoreAdapter credAdapter,
        out bool shouldDispose)
    {
        if (_clientFactory != null)
        {
            var baseClient = _clientFactory();
            var httpField = typeof(WakeMeshApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
            var tokensField = typeof(WakeMeshApiClient).GetField("_tokens", BindingFlags.Instance | BindingFlags.NonPublic);
            var baseHttp = httpField?.GetValue(baseClient) as HttpClient;
            var baseTokens = tokensField?.GetValue(baseClient) as IAccessTokenProvider;

            if (baseHttp != null && baseTokens != null)
            {
                var method = typeof(WakeMeshApiClient).GetMethod(
                    "CreateForTests",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    new[] { typeof(HttpClient), typeof(IAccessTokenProvider), typeof(IDeviceCredentialStore), typeof(bool) });
                if (method != null)
                {
                    shouldDispose = true;
                    return (WakeMeshApiClient)method.Invoke(null, new object?[] { baseHttp, baseTokens, credAdapter, false })!;
                }
            }

            shouldDispose = false;
            return baseClient;
        }

        shouldDispose = true;
        return WakeMeshApiClient.Create(startingUrl, _authService, credAdapter);
    }

    public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default)
    {
        return _registrationStore.LoadRegistrationAsync(cancellationToken);
    }

    private async Task<CloudSyncCleanupResult> CleanupOldScopeCoreAsync(
        CloudDeviceRegistration reg,
        CancellationToken cancellationToken)
    {
        var oldBinding = new CloudSyncBinding(
            reg.ApiBaseUrl,
            reg.AccountId,
            reg.ServerDeviceId,
            reg.RegistrationGeneration);

        var baseDir = _customStorageDir ?? AlarmStoragePathHelper.GetBaseStorageDirectory();
        var manifestPath = CloudSyncStoragePaths.Manifest(CloudSyncStoragePaths.ResolveBaseDirectory(baseDir), oldBinding);
        var revPath = CloudSyncStoragePaths.AppliedRevision(CloudSyncStoragePaths.ResolveBaseDirectory(baseDir), oldBinding);

        var manifestStore = new FileCloudSyncManifestStore(oldBinding, manifestPath);
        var revStore = new DurableAppliedRevisionStore(oldBinding, revPath);
        var cleanup = new CloudSyncBindingCleanup(oldBinding, _alarmCoordinator, manifestStore, revStore);
        var result = await cleanup.CleanupBindingAsync(cancellationToken).ConfigureAwait(false);

        if (_credentialStore is SecureDeviceCredentialStore secureStore)
        {
            try
            {
                var oldAccountBinding = new AccountBinding(
                    AccountBinding.NormalizeApiOrigin(reg.ApiBaseUrl),
                    _authService.CurrentBinding.ApiClientId,
                    _authService.CurrentBinding.AndroidClientId,
                    reg.AccountId);
                await secureStore.ClearCredentialsForBindingAsync(oldAccountBinding, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal credential purge error
            }
        }

        return result;
    }

    public async Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult<DeviceRegisterResponse>.Fail(
                ApiErrorKind.BadRequest,
                message: "クラウドへのサインインまたは接続設定が完了していません。設定画面をご確認ください。");
        }

        await _scopeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _registrationStore.LoadRegistrationAsync(CancellationToken.None).ConfigureAwait(false);
            if (existing != null && existing.RecoveryNeeded)
            {
                return ApiResult<DeviceRegisterResponse>.Fail(
                    ApiErrorKind.UnknownOutcome,
                    errorCode: "recoveryNeeded",
                    message: "前回の登録処理が不確定な状態で終了したため安全確認が必要です。多重登録を防止するため再登録はブロックされています。",
                    recoveryNeeded: true);
            }

            var startingBinding = _authService.CurrentBinding;
            var startingUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
            var startingAccountId = CurrentUserAccountId ?? string.Empty;

            // Serialize cleanup of previous scope if identity changed
            if (existing != null && !existing.MatchesScope(startingUrl, startingAccountId))
            {
                var cleanupResult = await CleanupOldScopeCoreAsync(existing, CancellationToken.None).ConfigureAwait(false);
                if (!cleanupResult.Succeeded)
                {
                    var warningReg = new CloudDeviceRegistration(
                        existing.ServerDeviceId,
                        existing.RegistrationGeneration,
                        existing.ApiBaseUrl,
                        existing.AccountId,
                        existing.DeviceDisplayName,
                        existing.RegisteredAt,
                        recoveryNeeded: true,
                        recoveryReason: "旧バインディングの同期クリーンアップに失敗しました。再試行が必要です。");

                    await _registrationStore.SaveRegistrationAsync(warningReg, CancellationToken.None).ConfigureAwait(false);
                    InvalidateObservationScope();
                    RegistrationChanged?.Invoke(this, EventArgs.Empty);

                    return ApiResult<DeviceRegisterResponse>.Fail(
                        ApiErrorKind.UnknownOutcome,
                        errorCode: "cleanupFailed",
                        message: "旧バインディングの同期クリーンアップに失敗しました。再試行が必要です。",
                        recoveryNeeded: true);
                }
            }

            var nextGeneration = (existing?.RegistrationGeneration ?? 0) + 1;
            var credAdapter = new ScopedRegistrationCredentialStoreAdapter(_credentialStore, startingBinding);
            var regClient = CreateRegistrationClient(startingBinding, startingUrl, credAdapter, out var disposeRegClient);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
            var ct = linkedCts.Token;

            ApiResult<DeviceRegisterResponse> regResult;
            try
            {
                regResult = await regClient.RegisterDeviceAsync(displayName, persistCredential: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                if (disposeRegClient)
                {
                    regClient.Dispose();
                }
            }

            // Post-success / post-attempt persistence must be done under a bounded independent token, NOT current ct
            using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var persistToken = persistCts.Token;

            if (!regResult.IsSuccess)
            {
                if (regResult.RecoveryNeeded)
                {
                    var recoveryReg = new CloudDeviceRegistration(
                        regResult.ServerDeviceId ?? (existing?.ServerDeviceId ?? Guid.NewGuid()),
                        nextGeneration,
                        startingUrl,
                        startingAccountId,
                        displayName,
                        DateTimeOffset.UtcNow,
                        recoveryNeeded: true,
                        recoveryReason: regResult.Message ?? "UnknownOutcome during registration");

                    await _registrationStore.SaveRegistrationAsync(recoveryReg, persistToken).ConfigureAwait(false);
                    InvalidateObservationScope();
                    RegistrationChanged?.Invoke(this, EventArgs.Empty);
                }

                return regResult;
            }

            var response = regResult.Value!;
            var registration = new CloudDeviceRegistration(
                response.Id,
                nextGeneration,
                startingUrl,
                startingAccountId,
                string.IsNullOrWhiteSpace(displayName) ? response.DisplayName : displayName,
                DateTimeOffset.UtcNow,
                recoveryNeeded: false,
                recoveryReason: null);

            await _registrationStore.SaveRegistrationAsync(registration, persistToken).ConfigureAwait(false);
            InvalidateObservationScope();
            RegistrationChanged?.Invoke(this, EventArgs.Empty);

            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            return regResult;
        }
        finally
        {
            _scopeGate.Release();
        }
    }

    public async Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _registrationStore.LoadRegistrationAsync(cancellationToken).ConfigureAwait(false);
        if (existing != null && existing.RecoveryNeeded)
        {
            var cleared = new CloudDeviceRegistration(
                existing.ServerDeviceId,
                existing.RegistrationGeneration,
                existing.ApiBaseUrl,
                existing.AccountId,
                existing.DeviceDisplayName,
                existing.RegisteredAt,
                recoveryNeeded: false,
                recoveryReason: null);
            await _registrationStore.SaveRegistrationAsync(cleared, cancellationToken).ConfigureAwait(false);
            InvalidateObservationScope();
            RegistrationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult<IReadOnlyList<DeviceListItem>>.Fail(
                ApiErrorKind.BadRequest,
                message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.ListDevicesAsync(linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult.Fail(ApiErrorKind.BadRequest, message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.RetireDeviceAsync(deviceId, linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult.Fail(ApiErrorKind.BadRequest, message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.RevokeDeviceAsync(deviceId, linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult<IReadOnlyList<AlarmGroupDto>>.Fail(
                ApiErrorKind.BadRequest,
                message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.ListAlarmGroupsAsync(linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
        AlarmGroupWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult<GroupSaveOutcome>.Fail(
                ApiErrorKind.BadRequest,
                message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.CreateAlarmGroupAsync(request, linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
        string groupId,
        AlarmGroupWriteRequest request,
        long ifMatchVersion,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult<GroupSaveOutcome>.Fail(
                ApiErrorKind.BadRequest,
                message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.ReplaceAlarmGroupAsync(groupId, request, ifMatchVersion, linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<ApiResult> DeleteAlarmGroupAsync(
        string groupId,
        long ifMatchVersion,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return ApiResult.Fail(ApiErrorKind.BadRequest, message: "サインインまたは接続設定が完了していません。");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var client = GetClient();
        return await client.DeleteAlarmGroupAsync(groupId, ifMatchVersion, linkedCts.Token).ConfigureAwait(false);
    }

    public async Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfiguredAndSignedIn)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, ApiErrorKind.Canceled, "notSignedIn");
        }

        var registration = await _registrationStore.LoadRegistrationAsync(cancellationToken).ConfigureAwait(false);
        if (registration == null || registration.RecoveryNeeded)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, ApiErrorKind.Canceled, "notRegisteredOrRecoveryNeeded");
        }

        // Validate immutable binding matches current settings and user
        var currentUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
        var currentAccount = CurrentUserAccountId ?? string.Empty;
        if (!registration.MatchesScope(currentUrl, currentAccount))
        {
            // Scope mismatch: prevent sending secrets or ACKs to mismatched server/user
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, ApiErrorKind.Canceled, "scopeMismatch");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inFlightCts.Token);
        var ct = linkedCts.Token;

        var binding = new CloudSyncBinding(
            registration.ApiBaseUrl,
            registration.AccountId,
            registration.ServerDeviceId,
            registration.RegistrationGeneration);

        var baseDir = CloudSyncStoragePaths.ResolveBaseDirectory(_customStorageDir);
        var manifestPath = CloudSyncStoragePaths.Manifest(baseDir, binding);
        var revPath = CloudSyncStoragePaths.AppliedRevision(baseDir, binding);

        var manifestStore = new FileCloudSyncManifestStore(binding, manifestPath);
        var applier = new MobileDesiredApplier(binding, _alarmCoordinator, manifestStore);
        var appliedStore = new DurableAppliedRevisionStore(binding, revPath);

        var client = GetClient();
        var orchestrator = new DesiredSyncOrchestrator(client, applier, appliedStore);

        // T118: only the outcome of this epoch may be published — an identity/registration change while the
        // request was in flight must not be reported as an observation of the new scope.
        var observationEpoch = Interlocked.Read(ref _observationEpoch);
        var outcome = await orchestrator.SyncDeviceAsync(binding.ServerDeviceId, ct).ConfigureAwait(false);
        TryPublishObservation(binding, outcome, observationEpoch);
        SyncCompleted?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    /// <summary>
    /// Hands the really-received desired snapshot to the alarm-list projection. Only statuses that carry a
    /// server answer are published; transport failures, cancellations and stale replies keep the previous
    /// observation (the projection then simply cannot claim anything about pending changes).
    /// </summary>
    private void TryPublishObservation(CloudSyncBinding binding, DesiredSyncOutcome outcome, long observationEpoch)
    {
        if (_observations is null || outcome.Snapshot is null)
        {
            return;
        }

        if (outcome.Status is not (
                DesiredSyncStatus.AppliedAcknowledged or
                DesiredSyncStatus.ApplyFailedAcknowledged or
                DesiredSyncStatus.ApplyIncompleteNoAck))
        {
            return;
        }

        if (Interlocked.Read(ref _observationEpoch) != observationEpoch)
        {
            return;
        }

        _observations.Publish(binding.ToStorageKey(), outcome.Snapshot, outcome.Status, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Called whenever the identity or the device registration changes: the previous observation no longer
    /// describes this scope, and a cycle that is still in flight must not publish afterwards.
    /// </summary>
    private void InvalidateObservationScope()
    {
        Interlocked.Increment(ref _observationEpoch);
        _observations?.Clear();
    }

    public void Dispose()
    {
        _authService.StateChanged -= OnAuthStateChanged;
        try
        {
            _inFlightCts.Cancel();
            _inFlightCts.Dispose();
        }
        catch { }

        _scopeGate.Dispose();

        if (_clientFactory == null && _cachedClient != null)
        {
            _cachedClient.Dispose();
            _cachedClient = null;
        }
    }
}
