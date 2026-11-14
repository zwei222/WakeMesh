using System;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    // Kept for DI constructor compatibility with IAuthSettingsStore registration (settings mutations go through IAuthService).
    private readonly IAuthSettingsStore _settingsStore;

    private string? _apiBaseUrl;
    private string? _androidClientId;
    private string? _apiClientId;
    private string? _validationMessage;
    private string? _connectionCheckResultText;
    private bool _isBusy;
    private bool _isSettingsReady;
    private Task? _initializeTask;
    private readonly object _initSync = new();

    public string? ApiBaseUrl
    {
        get => _apiBaseUrl;
        set
        {
            if (SetProperty(ref _apiBaseUrl, value))
            {
                OnPropertyChanged(nameof(IsRebuildNeeded));
                OnPropertyChanged(nameof(RebuildNotice));
            }
        }
    }

    public string? AndroidClientId
    {
        get => _androidClientId;
        set
        {
            if (SetProperty(ref _androidClientId, value))
            {
                OnPropertyChanged(nameof(IsRebuildNeeded));
                OnPropertyChanged(nameof(RebuildNotice));
            }
        }
    }

    public string? ApiClientId
    {
        get => _apiClientId;
        set => SetProperty(ref _apiClientId, value);
    }

    public string CompileTimeClientId => CompiledAuthConstants.CompileTimeClientId;

    public bool IsRebuildNeeded
    {
        get
        {
            var androidId = AccountBinding.NormalizeGuid(AndroidClientId);
            var compileId = AccountBinding.NormalizeGuid(CompiledAuthConstants.CompileTimeClientId);
            if (string.IsNullOrWhiteSpace(androidId) || AccountBinding.IsZeroOrEmptyGuid(androidId))
            {
                return false;
            }
            return !string.Equals(androidId, compileId, StringComparison.Ordinal);
        }
    }

    public string? RebuildNotice => IsRebuildNeeded
        ? $"【注意: 再ビルドが必要】現在のAPKはビルド時 WakeMeshAuthClientId ({CompiledAuthConstants.CompileTimeClientId}) でコンパイルされています。入力された Client ID で動作させるには、安全なローカルビルドコマンド (-p:WakeMeshAuthClientId={AndroidClientId}) で再ビルドしてください。"
        : null;

    public string AuthStateText => _authService.State switch
    {
        AuthState.NotConfigured => "未設定",
        AuthState.NeedsRebuild => "再ビルド必要",
        AuthState.SignedOut => "サインアウト済み",
        AuthState.SigningIn => "サインイン処理中...",
        AuthState.SignedIn => "サインイン済み",
        AuthState.ReauthRequired => "再認証待ち",
        _ => "不明"
    };

    public string StatusMessage => _authService.StatusMessage;

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public string? ConnectionCheckResultText
    {
        get => _connectionCheckResultText;
        private set => SetProperty(ref _connectionCheckResultText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>
    /// True after the first successful InitializeAsync that mirrored durable settings into the form.
    /// Save is blocked until this is true so a blank form cannot wipe disk.
    /// </summary>
    public bool IsSettingsReady
    {
        get => _isSettingsReady;
        private set
        {
            if (SetProperty(ref _isSettingsReady, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool CanSignIn => !IsBusy && IsSettingsReady && _authService.CanSignIn;
    public bool CanSignOut => !IsBusy && _authService.CanSignOut;

    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ClearSettingsCommand { get; }
    public AsyncRelayCommand SignInCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public AsyncRelayCommand CheckConnectionCommand { get; }

    public SettingsViewModel(IAuthService authService, IAuthSettingsStore settingsStore)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _ = _settingsStore;

        _authService.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AuthStateText));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(CanSignIn));
            OnPropertyChanged(nameof(CanSignOut));
            UpdateCommandStates();
        };

        // Do NOT copy settings in the constructor: durable load is async and must be awaited
        // via InitializeAsync (Settings page + app startup). Leaving fields unset prevents
        // blank-form saves from wiping existing settings before load completes.

        SaveSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy || !IsSettingsReady) return;
            IsBusy = true;
            try
            {
                var candidate = new AuthSettings
                {
                    ApiBaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl) ? null : ApiBaseUrl.Trim(),
                    AndroidClientId = string.IsNullOrWhiteSpace(AndroidClientId) ? null : AndroidClientId.Trim(),
                    ApiClientId = string.IsNullOrWhiteSpace(ApiClientId) ? null : ApiClientId.Trim(),
                };

                var validation = candidate.Validate();
                if (!validation.IsValid)
                {
                    ValidationMessage = validation.ErrorMessage;
                    return;
                }

                ValidationMessage = null;
                await _authService.ApplySettingsAsync(candidate);
                // Reflect normalized values from the service after apply.
                ApplySettingsToForm(_authService.Settings);
                ConnectionCheckResultText = "設定を保存しました。";
                OnPropertyChanged(nameof(IsRebuildNeeded));
                OnPropertyChanged(nameof(RebuildNotice));
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && IsSettingsReady);

        ClearSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await _authService.ClearSettingsAsync();
                ApiBaseUrl = null;
                AndroidClientId = null;
                ApiClientId = null;
                ValidationMessage = null;
                ConnectionCheckResultText = null;
                IsSettingsReady = true;
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy);

        SignInCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy || !IsSettingsReady) return;
            IsBusy = true;
            try
            {
                await _authService.InitializeAsync();
                await _authService.SignInInteractiveAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && IsSettingsReady && _authService.CanSignIn);

        SignOutCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await _authService.SignOutAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && _authService.CanSignOut);

        CheckConnectionCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy || !IsSettingsReady) return;
            IsBusy = true;
            ConnectionCheckResultText = "接続テスト実行中...";
            try
            {
                var result = await _authService.CheckConnectionAsync();
                ConnectionCheckResultText = result.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && IsSettingsReady);
    }

    /// <summary>
    /// Idempotent gate used by Settings page appearance and tests. Mirrors durable settings into the form.
    /// </summary>
    public Task InitializeAsync()
    {
        if (IsSettingsReady)
        {
            return Task.CompletedTask;
        }

        lock (_initSync)
        {
            _initializeTask ??= InitializeCoreAsync();
            return _initializeTask;
        }
    }

    private async Task InitializeCoreAsync()
    {
        await _authService.InitializeAsync();
        ApplySettingsToForm(_authService.Settings);
        IsSettingsReady = true;
        OnPropertyChanged(nameof(AuthStateText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(CanSignOut));
        OnPropertyChanged(nameof(IsRebuildNeeded));
        OnPropertyChanged(nameof(RebuildNotice));
        UpdateCommandStates();
    }

    private void ApplySettingsToForm(AuthSettings settings)
    {
        ApiBaseUrl = settings.ApiBaseUrl;
        AndroidClientId = settings.AndroidClientId;
        ApiClientId = settings.ApiClientId;
    }

    private void UpdateCommandStates()
    {
        SaveSettingsCommand.RaiseCanExecuteChanged();
        ClearSettingsCommand.RaiseCanExecuteChanged();
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        CheckConnectionCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(CanSignOut));
    }
}
