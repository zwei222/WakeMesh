using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Window lifecycle-driven foreground synchronization service.
/// Periodically synchronizes cloud alarms every ~15 seconds while the application is in the foreground,
/// without duplicate in-flight synchronization, canceling on pause/deactivation/logout/settings change.
/// Operates independently of whether CloudManagementPage is currently loaded.
/// </summary>
public sealed class CloudForegroundSyncService : ICloudForegroundSyncService
{
    public static readonly TimeSpan DefaultSyncInterval = TimeSpan.FromSeconds(15);

    private readonly ICloudManagementService _cloudService;
    private readonly IAuthService _authService;
    private readonly TimeSpan _syncInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayFunc;

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _currentSyncCts;
    private CancellationTokenSource? _loopCts;
    private Task? _timerLoopTask;
    private bool _isInForeground;
    private bool _isDisposed;

    private Window? _attachedWindow;
    public bool HasNativeWindowAttached => _attachedWindow != null;

    public bool IsRunning => _timerLoopTask != null && !_timerLoopTask.IsCompleted;
    public bool IsInForeground => _isInForeground;
    public bool IsSyncInProgress => _syncGate.CurrentCount == 0;

    public CloudForegroundSyncService(
        ICloudManagementService cloudService,
        IAuthService authService,
        TimeSpan? syncInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null)
    {
        _cloudService = cloudService ?? throw new ArgumentNullException(nameof(cloudService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _syncInterval = syncInterval ?? DefaultSyncInterval;
        _delayFunc = delayFunc ?? ((delay, ct) => Task.Delay(delay, ct));

        _authService.StateChanged += OnAuthStateChanged;
    }

    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_stateLock)
        {
            if (_attachedWindow == window) return;

            DetachWindow();
            _attachedWindow = window;

            window.Created += OnWindowCreated;
            window.Resumed += OnWindowResumed;
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
            window.Stopped += OnWindowStopped;
            window.Destroying += OnWindowDestroying;

            Resume();
        }
    }

    private void DetachWindow()
    {
        if (_attachedWindow != null)
        {
            _attachedWindow.Created -= OnWindowCreated;
            _attachedWindow.Resumed -= OnWindowResumed;
            _attachedWindow.Activated -= OnWindowActivated;
            _attachedWindow.Deactivated -= OnWindowDeactivated;
            _attachedWindow.Stopped -= OnWindowStopped;
            _attachedWindow.Destroying -= OnWindowDestroying;
            _attachedWindow = null;
        }
    }

    private void OnWindowCreated(object? sender, EventArgs e) => Resume();
    private void OnWindowResumed(object? sender, EventArgs e) => Resume();
    private void OnWindowActivated(object? sender, EventArgs e) => Resume();
    private void OnWindowDeactivated(object? sender, EventArgs e) => Pause();
    private void OnWindowStopped(object? sender, EventArgs e) => Pause();
    private void OnWindowDestroying(object? sender, EventArgs e)
    {
        Pause();
        DetachWindow();
    }

    public void OnAppResumed() => Resume();
    public void OnAppPaused() => Pause();

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        // Cancel in-flight sync immediately when settings change or sign-out occurs
        CancelInFlightSync();
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;
            _isInForeground = true;

            // Prevent duplicate timer loop if already running and active
            if (_timerLoopTask != null && !_timerLoopTask.IsCompleted && _loopCts != null && !_loopCts.IsCancellationRequested)
            {
                return;
            }

            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var loopToken = _loopCts.Token;

            _timerLoopTask = Task.Run(() => PeriodicSyncLoopAsync(loopToken), loopToken);
        }
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            _isInForeground = false;
            _loopCts?.Cancel();
            CancelInFlightSync();
        }
    }

    private void CancelInFlightSync()
    {
        lock (_stateLock)
        {
            try
            {
                _currentSyncCts?.Cancel();
                _currentSyncCts?.Dispose();
            }
            catch { }
            _currentSyncCts = null;
        }
    }

    private async Task PeriodicSyncLoopAsync(CancellationToken cancellationToken)
    {
        // Initial sync on foreground entry
        try
        {
            await TriggerSyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }

        while (!cancellationToken.IsCancellationRequested && _isInForeground)
        {
            try
            {
                await _delayFunc(_syncInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested || !_isInForeground)
            {
                break;
            }

            try
            {
                await TriggerSyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { }
        }
    }

    public async Task<DesiredSyncOutcome?> TriggerSyncAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !_isInForeground)
        {
            return null;
        }

        // Gate: URL unset, not logged in, or unregistered -> zero network requests
        if (!_cloudService.IsConfiguredAndSignedIn)
        {
            return null;
        }

        var reg = await _cloudService.GetCurrentRegistrationAsync(cancellationToken).ConfigureAwait(false);
        if (reg == null || reg.RecoveryNeeded)
        {
            return null;
        }

        var currentUrl = _authService.Settings.ApiBaseUrl ?? string.Empty;
        var currentAccount = _cloudService.CurrentUserAccountId ?? string.Empty;
        if (!reg.MatchesScope(currentUrl, currentAccount))
        {
            return null;
        }

        // Prevent overlapping/concurrent sync
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        CancellationTokenSource syncCts;
        lock (_stateLock)
        {
            CancelInFlightSync();
            _currentSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            syncCts = _currentSyncCts;
        }

        try
        {
            return await _cloudService.SyncCurrentDeviceAsync(syncCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _authService.StateChanged -= OnAuthStateChanged;
#if ANDROID || IOS || MACCATALYST || WINDOWS
            DetachWindow();
#endif
            Pause();

            _syncGate.Dispose();
        }
    }
}
