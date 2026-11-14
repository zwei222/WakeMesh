using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;

namespace WakeMesh.Mobile.ViewModels;

public sealed class CloudManagementViewModel : ViewModelBase
{
    private readonly ICloudManagementService _cloudService;
    private readonly IAuthService _authService;
    private readonly ICloudNavigation _navigation;
    private readonly IMainThreadDispatcher _dispatcher;

    private bool _isBusy;
    private bool _isSyncing;
    private bool _isRegistered;
    private Guid? _currentServerDeviceId;
    private long _registrationGeneration;
    private string _currentDeviceDisplayName = string.Empty;
    private bool _isRecoveryNeeded;
    private string? _recoveryReason;
    private string _newDeviceDisplayName = string.Empty;

    private string? _statusMessage;
    private string? _lastSyncStatusMessage;
    private string? _lastSyncTimeText;
    private string _lastSyncStateKey = "unknown";

    private bool _isDeviceListError;
    private string? _deviceListErrorMessage;
    private bool _isGroupListError;
    private string? _groupListErrorMessage;

    public bool IsBusy
    {
        get => _isBusy;
        set => OnUiThread(() =>
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        });
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        set => OnUiThread(() =>
        {
            if (SetProperty(ref _isSyncing, value))
            {
                UpdateCommandStates();
            }
        });
    }

    public bool IsConfiguredAndSignedIn => _cloudService.IsConfiguredAndSignedIn;
    public bool NeedsConfigurationOrSignIn => !IsConfiguredAndSignedIn;

    public string AuthStateText => _authService.State switch
    {
        AuthState.NotConfigured => "接続未設定 (設定が必要です)",
        AuthState.NeedsRebuild => "再ビルド必要",
        AuthState.SignedOut => "サインアウト中 (サインインが必要です)",
        AuthState.SigningIn => "サインイン処理中...",
        AuthState.SignedIn => "サインイン済み (接続有効)",
        AuthState.ReauthRequired => "サインインし直してください",
        _ => "未認証"
    };

    /// <summary>
    /// T80 P1-1: driven by the real authentication state (not by the configured flag), so the cloud
    /// banner can ask for a new sign-in instead of showing a generic unconfigured state.
    /// </summary>
    public bool IsReauthRequired => _authService.State == AuthState.ReauthRequired;

    public bool IsRegistered
    {
        get => _isRegistered;
        private set
        {
            if (SetProperty(ref _isRegistered, value))
            {
                OnPropertyChanged(nameof(NotRegistered));
            }
        }
    }

    public bool NotRegistered => !IsRegistered;

    public Guid? CurrentServerDeviceId
    {
        get => _currentServerDeviceId;
        private set
        {
            if (SetProperty(ref _currentServerDeviceId, value))
            {
                OnPropertyChanged(nameof(CurrentServerDeviceIdString));
            }
        }
    }

    public string CurrentServerDeviceIdString => CurrentServerDeviceId?.ToString("D") ?? "未登録";

    public long RegistrationGeneration
    {
        get => _registrationGeneration;
        private set => SetProperty(ref _registrationGeneration, value);
    }

    public string CurrentDeviceDisplayName
    {
        get => _currentDeviceDisplayName;
        private set => SetProperty(ref _currentDeviceDisplayName, value);
    }

    public bool IsRecoveryNeeded
    {
        get => _isRecoveryNeeded;
        private set => SetProperty(ref _isRecoveryNeeded, value);
    }

    public string? RecoveryReason
    {
        get => _recoveryReason;
        private set => SetProperty(ref _recoveryReason, value);
    }

    public string NewDeviceDisplayName
    {
        get => _newDeviceDisplayName;
        set => SetProperty(ref _newDeviceDisplayName, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => OnUiThread(() => SetProperty(ref _statusMessage, value));
    }

    public string? LastSyncStatusMessage
    {
        get => _lastSyncStatusMessage;
        set => OnUiThread(() => SetProperty(ref _lastSyncStatusMessage, value));
    }

    public string? LastSyncTimeText
    {
        get => _lastSyncTimeText;
        set => OnUiThread(() => SetProperty(ref _lastSyncTimeText, value));
    }

    /// <summary>
    /// T99: the mark next to the sync message follows the last sync outcome (T80 §4 keys) instead of
    /// a fixed "pending". Only this view model may set it; the page just binds to it.
    /// </summary>
    public string LastSyncStateKey
    {
        get => _lastSyncStateKey;
        private set => OnUiThread(() => SetProperty(ref _lastSyncStateKey, value));
    }

    public bool IsDeviceListError
    {
        get => _isDeviceListError;
        private set => OnUiThread(() => SetProperty(ref _isDeviceListError, value));
    }

    public string? DeviceListErrorMessage
    {
        get => _deviceListErrorMessage;
        private set => OnUiThread(() => SetProperty(ref _deviceListErrorMessage, value));
    }

    public bool IsGroupListError
    {
        get => _isGroupListError;
        private set => OnUiThread(() => SetProperty(ref _isGroupListError, value));
    }

    public string? GroupListErrorMessage
    {
        get => _groupListErrorMessage;
        private set => OnUiThread(() => SetProperty(ref _groupListErrorMessage, value));
    }

    public string FcmNoticeText =>
        "プッシュ通知は未接続です。現在、同期はアプリを前面に戻したときと、手動で同期したときだけ実行されます。バックグラウンドでのリアルタイム同期は未完了です。";

    public ObservableCollection<CloudDeviceItemViewModel> Devices { get; } = new();
    public ObservableCollection<CloudAlarmGroupItemViewModel> AlarmGroups { get; } = new();

    public AsyncRelayCommand LoadDataCommand { get; }
    public AsyncRelayCommand RegisterCurrentDeviceCommand { get; }
    public AsyncRelayCommand ClearRecoveryCommand { get; }
    public AsyncRelayCommand ManualSyncCommand { get; }
    public AsyncRelayCommand<Guid> RetireDeviceCommand { get; }
    public AsyncRelayCommand<Guid> RevokeDeviceCommand { get; }
    public AsyncRelayCommand<CloudAlarmGroupItemViewModel> DeleteGroupCommand { get; }
    public AsyncRelayCommand NavigateToSettingsCommand { get; }
    public AsyncRelayCommand NavigateToCreateGroupCommand { get; }
    public AsyncRelayCommand<CloudAlarmGroupItemViewModel> NavigateToEditGroupCommand { get; }

    public CloudManagementViewModel(
        ICloudManagementService cloudService,
        IAuthService authService,
        ICloudNavigation? navigation = null,
        IMainThreadDispatcher? dispatcher = null)
    {
        _cloudService = cloudService ?? throw new ArgumentNullException(nameof(cloudService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _navigation = navigation ?? new MauiShellCloudNavigation();
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();

        _authService.StateChanged += OnAuthStateChanged;
        _cloudService.RegistrationChanged += OnRegistrationChanged;
        _cloudService.SyncCompleted += OnSyncCompleted;

        NavigateToSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            await _navigation.GoToAsync("SettingsPage").ConfigureAwait(false);
        });

        NavigateToCreateGroupCommand = new AsyncRelayCommand(async _ =>
        {
            await _navigation.GoToAsync("CloudAlarmGroupEditPage").ConfigureAwait(false);
        }, _ => IsConfiguredAndSignedIn);

        NavigateToEditGroupCommand = new AsyncRelayCommand<CloudAlarmGroupItemViewModel>(async group =>
        {
            if (group == null) return;
            await _navigation.GoToAsync($"CloudAlarmGroupEditPage?groupId={Uri.EscapeDataString(group.Id)}").ConfigureAwait(false);
        }, group => IsConfiguredAndSignedIn && group != null);

        LoadDataCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await RefreshRegistrationStateAsync().ConfigureAwait(false);

                if (!IsConfiguredAndSignedIn)
                {
                    StatusMessage = "クラウド接続が設定されていません。設定画面でサインインしてください。";
                    Devices.Clear();
                    AlarmGroups.Clear();
                    return;
                }

                await LoadCloudDataCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy);

        RegisterCurrentDeviceCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            if (!IsConfiguredAndSignedIn)
            {
                StatusMessage = "登録するにはサインインが必要です。設定画面をご確認ください。";
                return;
            }

            if (IsRecoveryNeeded)
            {
                StatusMessage = "前回の登録処理が不確定な状態です。再登録連打を防止するためブロックされています。";
                return;
            }

            IsBusy = true;
            StatusMessage = "端末をクラウドに登録中...";
            try
            {
                var nameToUse = string.IsNullOrWhiteSpace(NewDeviceDisplayName)
                    ? "この端末"
                    : NewDeviceDisplayName.Trim();

                var result = await _cloudService.RegisterCurrentDeviceAsync(nameToUse).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    if (result.RecoveryNeeded)
                    {
                        IsRecoveryNeeded = true;
                        RecoveryReason = result.Message ?? "登録処理の完了確認が必要です";
                        StatusMessage = "【多重登録防止】登録結果の確認が必要です。再登録はブロックされました。";
                    }
                    else
                    {
                        StatusMessage = $"端末登録失敗: {result.Message}";
                    }
                    return;
                }

                StatusMessage = $"端末登録完了 (ID: {result.Value!.Id})";
                NewDeviceDisplayName = string.Empty;
                await RefreshRegistrationStateAsync().ConfigureAwait(false);
                await LoadCloudDataCoreAsync().ConfigureAwait(false);

                // Run initial sync after registration
                await RunSyncCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && IsConfiguredAndSignedIn && !IsRecoveryNeeded);

        ClearRecoveryCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await _cloudService.ClearCurrentRegistrationRecoveryAsync().ConfigureAwait(false);
                await RefreshRegistrationStateAsync().ConfigureAwait(false);
                StatusMessage = "確認フラグをリセットしました。";
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && IsRecoveryNeeded);

        ManualSyncCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy || IsSyncing) return;
            if (!IsConfiguredAndSignedIn || !IsRegistered)
            {
                StatusMessage = "端末が登録されていないか、サインインが完了していません。";
                return;
            }

            IsSyncing = true;
            try
            {
                await RunSyncCoreAsync().ConfigureAwait(false);
                await LoadCloudDataCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                IsSyncing = false;
            }
        }, _ => !IsBusy && !IsSyncing && IsConfiguredAndSignedIn && IsRegistered);

        RetireDeviceCommand = new AsyncRelayCommand<Guid>(async deviceId =>
        {
            if (IsBusy || deviceId == Guid.Empty) return;
            IsBusy = true;
            try
            {
                var result = await _cloudService.RetireDeviceAsync(deviceId).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    StatusMessage = $"登録解除に失敗しました: {result.Message}";
                    return;
                }
                StatusMessage = "端末の登録解除を開始しました（端末での解除待ち）。";
                await LoadCloudDataCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }, devId => !IsBusy && IsConfiguredAndSignedIn && devId != Guid.Empty);

        RevokeDeviceCommand = new AsyncRelayCommand<Guid>(async deviceId =>
        {
            if (IsBusy || deviceId == Guid.Empty) return;
            IsBusy = true;
            try
            {
                var result = await _cloudService.RevokeDeviceAsync(deviceId).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    StatusMessage = $"接続の無効化に失敗しました: {result.Message}";
                    return;
                }
                StatusMessage = "端末の接続を無効にするよう要求しました（端末のアラーム削除は未確認）。";
                await LoadCloudDataCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }, devId => !IsBusy && IsConfiguredAndSignedIn && devId != Guid.Empty);

        DeleteGroupCommand = new AsyncRelayCommand<CloudAlarmGroupItemViewModel>(async group =>
        {
            if (IsBusy || group == null) return;
            IsBusy = true;
            try
            {
                var result = await _cloudService.DeleteAlarmGroupAsync(group.Id, group.Version).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    StatusMessage = $"グループ削除失敗: {result.Message}";
                    return;
                }
                StatusMessage = $"グループ '{group.Label}' を削除しました。";
                await LoadCloudDataCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }, g => !IsBusy && IsConfiguredAndSignedIn && g != null);

        // CanExecuteChanged drives IsEnabled bindings, so it must reach them on the UI thread too.
        // (The non-generic AsyncRelayCommand lives in ViewModelBase.cs, outside this fix's file scope.)
        foreach (var command in new IMainThreadMarshalledCommand[]
                 {
                     RetireDeviceCommand, RevokeDeviceCommand, DeleteGroupCommand, NavigateToEditGroupCommand
                 })
        {
            command.Marshal = OnUiThread;
        }
    }

    public CloudManagementViewModel(
        ICloudManagementService cloudService,
        IAuthService authService,
        Action<string>? navigationHandler)
        : this(cloudService, authService, navigationHandler != null ? new ActionCloudNavigation(navigationHandler) : null)
    {
    }

    /// <summary>
    /// Runs a UI-bound update on the main thread. Every awaited call in this view model uses
    /// ConfigureAwait(false), so without this the ObservableCollection mutations and
    /// PropertyChanged notifications raised after an await land on a thread-pool thread while
    /// MAUI reads them from the UI thread (reported reload crash).
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (_dispatcher.IsDispatchRequired) _dispatcher.Post(action);
        else action();
    }

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(IsConfiguredAndSignedIn));
        OnPropertyChanged(nameof(NeedsConfigurationOrSignIn));
        OnPropertyChanged(nameof(IsRegistered));
        OnPropertyChanged(nameof(NotRegistered));
        OnPropertyChanged(nameof(AuthStateText));
        OnPropertyChanged(nameof(IsReauthRequired));
        LoadDataCommand.RaiseCanExecuteChanged();
        RegisterCurrentDeviceCommand.RaiseCanExecuteChanged();
        ClearRecoveryCommand.RaiseCanExecuteChanged();
        ManualSyncCommand.RaiseCanExecuteChanged();
        NavigateToCreateGroupCommand.RaiseCanExecuteChanged();
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        OnUiThread(() =>
        {
            UpdateCommandStates();

            if (!IsConfiguredAndSignedIn)
            {
                Devices.Clear();
                AlarmGroups.Clear();
                StatusMessage = "サインインが解除されました。";
            }
        });
    }

    private void OnRegistrationChanged(object? sender, EventArgs e)
    {
        _ = RefreshRegistrationStateAsync();
    }

    private void OnSyncCompleted(object? sender, EventArgs e)
    {
        var timeText = DateTimeOffset.Now.ToString("HH:mm:ss");
        OnUiThread(() => LastSyncTimeText = timeText);
    }

    public async Task InitializeAsync()
    {
        await RefreshRegistrationStateAsync().ConfigureAwait(false);

        if (IsConfiguredAndSignedIn)
        {
            await LoadCloudDataCoreAsync().ConfigureAwait(false);

            if (IsRegistered)
            {
                await RunSyncCoreAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task RefreshRegistrationStateAsync()
    {
        var reg = await _cloudService.GetCurrentRegistrationAsync().ConfigureAwait(false);
        OnUiThread(() => ApplyRegistration(reg));
    }

    private void ApplyRegistration(CloudDeviceRegistration? reg)
    {
        if (reg != null)
        {
            IsRegistered = true;
            CurrentServerDeviceId = reg.ServerDeviceId;
            CurrentDeviceDisplayName = reg.DeviceDisplayName;
            RegistrationGeneration = reg.RegistrationGeneration;
            IsRecoveryNeeded = reg.RecoveryNeeded;
            RecoveryReason = reg.RecoveryReason;
        }
        else
        {
            IsRegistered = false;
            CurrentServerDeviceId = null;
            CurrentDeviceDisplayName = string.Empty;
            RegistrationGeneration = 0;
            IsRecoveryNeeded = false;
            RecoveryReason = null;
        }

        UpdateCommandStates();
    }

    private async Task LoadCloudDataCoreAsync()
    {
        // 1. Fetch devices
        var devResult = await _cloudService.ListDevicesAsync().ConfigureAwait(false);
        var deviceMap = new Dictionary<Guid, string>();
        var statusMap = new Dictionary<Guid, DeviceListItem>();
        var newDevices = new List<CloudDeviceItemViewModel>();

        if (devResult.IsSuccess)
        {
            var currentId = CurrentServerDeviceId;
            foreach (var d in devResult.Value ?? Array.Empty<DeviceListItem>())
            {
                deviceMap[d.Id] = d.DisplayName;
                statusMap[d.Id] = d;
                newDevices.Add(new CloudDeviceItemViewModel(d, isCurrentDevice: currentId.HasValue && d.Id == currentId.Value));
            }
        }

        // 2. Fetch alarm groups
        var grpResult = await _cloudService.ListAlarmGroupsAsync().ConfigureAwait(false);
        var newGroups = new List<CloudAlarmGroupItemViewModel>();
        if (grpResult.IsSuccess)
        {
            foreach (var g in grpResult.Value ?? Array.Empty<AlarmGroupDto>())
            {
                newGroups.Add(new CloudAlarmGroupItemViewModel(g, deviceMap, statusMap));
            }
        }

        // Bindings only observe these collections from the UI thread.
        OnUiThread(() =>
        {
            IsDeviceListError = !devResult.IsSuccess;
            DeviceListErrorMessage = devResult.IsSuccess ? null : $"端末一覧取得失敗: {devResult.Message}";
            if (devResult.IsSuccess)
            {
                Devices.Clear();
                foreach (var device in newDevices) Devices.Add(device);
            }

            IsGroupListError = !grpResult.IsSuccess;
            GroupListErrorMessage = grpResult.IsSuccess ? null : $"アラームグループ取得失敗: {grpResult.Message}";
            if (grpResult.IsSuccess)
            {
                AlarmGroups.Clear();
                foreach (var group in newGroups) AlarmGroups.Add(group);
            }
        });
    }

    private async Task RunSyncCoreAsync()
    {
        // T99: the band shows "pending" while the cycle is in flight, then the real outcome key.
        LastSyncStateKey = "pending";
        LastSyncStatusMessage = "同期中...";
        var outcome = await _cloudService.SyncCurrentDeviceAsync().ConfigureAwait(false);

        LastSyncTimeText = DateTimeOffset.Now.ToString("HH:mm:ss");
        LastSyncStateKey = outcome.Status switch
        {
            DesiredSyncStatus.AppliedAcknowledged => "ok",
            DesiredSyncStatus.ApplyFailedAcknowledged => "critical",
            DesiredSyncStatus.ApplyIncompleteNoAck => "critical",
            DesiredSyncStatus.TransportFailed => "warn",
            DesiredSyncStatus.CanceledNoAck => "unknown",
            DesiredSyncStatus.StaleRevisionSkipped => "unknown",
            _ => "unknown"
        };
        LastSyncStatusMessage = outcome.Status switch
        {
            DesiredSyncStatus.AppliedAcknowledged => $"同期完了 (ACK送信済: rev {outcome.Snapshot?.Revision})",
            DesiredSyncStatus.ApplyFailedAcknowledged => "ローカル適用一部失敗 (サーバーへ失敗ACK報告済)",
            DesiredSyncStatus.ApplyIncompleteNoAck => "ローカル適用中断 (ACK未送信)",
            DesiredSyncStatus.CanceledNoAck => "同期がキャンセルされました",
            DesiredSyncStatus.TransportFailed => "サーバー通信失敗 (オフラインまたは接続エラー)",
            DesiredSyncStatus.StaleRevisionSkipped => "同期スキップ (適用済みより古いリビジョン)",
            _ => $"状態: {outcome.Status}"
        };
    }
}

/// <summary>Commands that must raise CanExecuteChanged on the main thread (MAUI bindings).</summary>
public interface IMainThreadMarshalledCommand
{
    Action<Action>? Marshal { get; set; }
}

public sealed class AsyncRelayCommand<T> : System.Windows.Input.ICommand, IMainThreadMarshalledCommand
{
    private readonly Func<T, Task> _execute;
    private readonly Func<T, bool>? _canExecute;
    private bool _isExecuting;

    public Action<Action>? Marshal { get; set; }

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<T, Task> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        if (parameter is T val)
        {
            return _canExecute?.Invoke(val) ?? true;
        }
        if (parameter is null && default(T) is null)
        {
            return _canExecute?.Invoke(default!) ?? true;
        }
        return false;
    }

    public async void Execute(object? parameter)
    {
        if (parameter is T val)
        {
            await ExecuteAsync(val);
        }
        else if (parameter is null && default(T) is null)
        {
            await ExecuteAsync(default!);
        }
    }

    public async Task ExecuteAsync(T parameter)
    {
        if (!CanExecute(parameter)) return;

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;
        if (Marshal is null) handler(this, EventArgs.Empty);
        else Marshal(() => handler(this, EventArgs.Empty));
    }
}

public sealed class RelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter is T val) return _canExecute?.Invoke(val) ?? true;
        if (parameter is null && default(T) is null) return _canExecute?.Invoke(default!) ?? true;
        return false;
    }

    public void Execute(object? parameter)
    {
        if (parameter is T val) _execute(val);
        else if (parameter is null && default(T) is null) _execute(default!);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
