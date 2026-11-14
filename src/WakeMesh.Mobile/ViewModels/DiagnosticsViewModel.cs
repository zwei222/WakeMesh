using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.ViewModels;

public sealed class DiagnosticsViewModel : ViewModelBase
{
    private readonly IPermissionService _permissionService;
    private readonly IHolidayService _holidayService;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly AlarmCoordinator? _alarmCoordinator;
    private readonly IAuthService? _authService;

    private bool _isBusy;
    private string? _rescheduledMessage;
    private string? _holidayOperationMessage;

    public string DeviceId => _deviceInfoProvider.DeviceId;
    public string DeviceDisplayName => _deviceInfoProvider.DeviceDisplayName;
    public string DeviceName => _deviceInfoProvider.DeviceDisplayName;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RequestNotificationCommand.RaiseCanExecuteChanged();
                OpenExactAlarmSettingsCommand.RaiseCanExecuteChanged();
                OpenFullScreenSettingsCommand.RaiseCanExecuteChanged();
                OpenBatteryOptimizationSettingsCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
                RefreshHolidaysCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OsVersionText
    {
        get
        {
            var api = _permissionService.OsApiLevel;
            var name = api switch
            {
                >= 36 => $"Android 16 (API {api})",
                35 => $"Android 15 (API {api})",
                34 => $"Android 14 (API {api})",
                33 => $"Android 13 (API {api})",
                31 or 32 => $"Android 12 (API {api})",
                _ => $"Android (API {api})"
            };
            return name;
        }
    }

    public bool IsExactAlarmGranted => _permissionService.CanScheduleExactAlarms;
    public bool IsNotificationGranted => _permissionService.HasNotificationPermission;
    public bool IsBatteryOptimizationIgnored => _permissionService.IsIgnoringBatteryOptimizations;
    public bool CanUseFullScreenIntent => _permissionService.CanUseFullScreenIntent;

    public bool CanRequestNotification => _permissionService.OsApiLevel >= 33 && !IsNotificationGranted;
    public bool CanOpenExactAlarmSettings => _permissionService.OsApiLevel >= 31 && !IsExactAlarmGranted;
    public bool CanOpenFullScreenSettings => _permissionService.OsApiLevel >= 34 && !CanUseFullScreenIntent;
    // T70: battery/Doze row had a status label but no way to reach the OS setting at all.
    public bool CanOpenBatteryOptimizationSettings => _permissionService.OsApiLevel >= 23 && !IsBatteryOptimizationIgnored;

    public string ExactAlarmStatus => _permissionService.OsApiLevel < 31
        ? "不要 (Android 11以下は対象外)"
        : (IsExactAlarmGranted ? "許可済み (AlarmClock)" : "未許可 (設定から許可が必要)");

    public string NotificationStatus => _permissionService.OsApiLevel < 33
        ? "不要 (Android 12以下は実行時許可不要)"
        : (IsNotificationGranted ? "許可済み" : "未許可 (通知権限の許可が必要)");

    public string BatteryStatus => _permissionService.OsApiLevel < 23
        ? "不要 (OS対象外)"
        : (IsBatteryOptimizationIgnored ? "最適化除外済み (推奨)" : "未設定 (Doze制限の可能性あり)");

    public string FullScreenStatus => _permissionService.OsApiLevel < 34
        ? "不要 (Android 13以下はインストール時許可)"
        : (CanUseFullScreenIntent ? "許可済み" : "未許可 (全画面表示の許可が必要)");

    public string NotificationActionText => _permissionService.OsApiLevel < 33
        ? "不要 (OS対象外)"
        : (IsNotificationGranted ? "許可済み" : "通知権限をリクエスト");

    public string ExactAlarmActionText => _permissionService.OsApiLevel < 31
        ? "不要 (OS対象外)"
        : (IsExactAlarmGranted ? "許可済み" : "正確なアラーム設定を開く");

    public string FullScreenActionText => _permissionService.OsApiLevel < 34
        ? "不要 (OS対象外)"
        : (CanUseFullScreenIntent ? "許可済み" : "全画面アラーム設定を開く");

    public string BatteryActionText => _permissionService.OsApiLevel < 23
        ? "不要 (OS対象外)"
        : (IsBatteryOptimizationIgnored ? "最適化除外済み" : "省電力除外を設定");

    // T80 §4 state marks: one read-only key per row, derived from the same OS gates the status text
    // already uses. The pages bind these to Controls/StateIndicator and never choose a colour.
    // Android 12 (API 32) and below: the runtime permission does not exist → notRequired.
    public string ExactAlarmStateKey => _permissionService.OsApiLevel < 31
        ? "notRequired"
        : (IsExactAlarmGranted ? "ok" : "critical");

    public string NotificationStateKey => _permissionService.OsApiLevel < 33
        ? "notRequired"
        : (IsNotificationGranted ? "ok" : "warn");

    public string FullScreenStateKey => _permissionService.OsApiLevel < 34
        ? "notRequired"
        : (CanUseFullScreenIntent ? "ok" : "warn");

    public string BatteryStateKey => _permissionService.OsApiLevel < 23
        ? "notRequired"
        : (IsBatteryOptimizationIgnored ? "ok" : "warn");

    // §4 S5 summary: ok only when every gate is satisfied, critical when the exact-alarm gate is the
    // one that is missing, warn otherwise.
    public string PermissionSummaryStateKey =>
        IsAllPermissionsHealthy
            ? "ok"
            : (_permissionService.OsApiLevel >= 31 && !IsExactAlarmGranted ? "critical" : "warn");

    // §4 S5 holiday row: 未取得（fail-open）は warn、取得済みは ok。
    public string HolidayStateKey => _holidayService.HasHolidayData ? "ok" : "warn";

    // T80 P1-3: the summary must not say "all set" while the full-screen alarm permission is missing.
    public bool IsAllPermissionsHealthy =>
        (_permissionService.OsApiLevel < 31 || IsExactAlarmGranted) &&
        (_permissionService.OsApiLevel < 33 || IsNotificationGranted) &&
        (_permissionService.OsApiLevel < 34 || CanUseFullScreenIntent) &&
        IsBatteryOptimizationIgnored;

    public string PermissionStatusSummary
    {
        get
        {
            if (IsAllPermissionsHealthy)
            {
                // Item 11: Do not assert absolute exact ringing without caveat
                return "アラームを鳴らす準備ができています（端末の省電力設定やDoze状態により影響を受ける場合があります）。";
            }

            var missing = new List<string>();
            if (_permissionService.OsApiLevel >= 31 && !IsExactAlarmGranted) missing.Add("正確なアラーム設定権限");
            if (_permissionService.OsApiLevel >= 33 && !IsNotificationGranted) missing.Add("通知権限");
            if (!IsBatteryOptimizationIgnored) missing.Add("電池最適化の除外");
            if (_permissionService.OsApiLevel >= 34 && !CanUseFullScreenIntent) missing.Add("全画面アラーム表示権限");

            return $"アラームを鳴らすには、次の設定を確認してください。{string.Join("、", missing)}。";
        }
    }

    public string HolidayStatus => _holidayService.HolidayStatusSummary;
    public string HolidayStatusSummary => _holidayService.HolidayStatusSummary;

    /// <summary>T80 P1-2: true while the holiday calendar is unknown, so holidays still ring.</summary>
    public bool IsHolidayDataMissing => !_holidayService.HasHolidayData;

    public string HolidayCoverageNotice
    {
        get
        {
            if (_holidayService.Calendar is null)
            {
                return "祝日情報を取得するまで、祝日のスキップは適用されません（祝日もアラーム対象）。";
            }

            var cal = _holidayService.Calendar;
            return $"カバレッジ: {cal.CoverageStartInclusive:yyyy/MM/dd}〜{cal.CoverageEndInclusive:yyyy/MM/dd} (内閣府公表)。" +
                   $"※ 内閣府CSV仕様により最終祝日（{cal.CoverageEndInclusive:yyyy/MM/dd}）以降の年末はCore仕様上カバレッジ外（未判定・祝日もアラーム対象）となる制約があります。";
        }
    }

    public string? RescheduledMessage
    {
        get => _rescheduledMessage;
        private set => SetProperty(ref _rescheduledMessage, value);
    }

    public string? HolidayOperationMessage
    {
        get => _holidayOperationMessage;
        private set => SetProperty(ref _holidayOperationMessage, value);
    }

    // 正直なクラウドステータス（Mesh P2P等未要求機能は撤去）
    public string AuthStatus => _authService != null ? _authService.StatusMessage : "未接続 (Microsoft Entra ID 未設定)";
    public string CloudSyncStatus => "未接続 (ローカル単独運用中)";

    public AsyncRelayCommand RequestNotificationCommand { get; }
    public AsyncRelayCommand OpenExactAlarmSettingsCommand { get; }
    public AsyncRelayCommand OpenFullScreenSettingsCommand { get; }
    public AsyncRelayCommand OpenBatteryOptimizationSettingsCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RefreshHolidaysCommand { get; }

    public DiagnosticsViewModel(
        IPermissionService permissionService,
        IHolidayService holidayService,
        IDeviceInfoProvider? deviceInfoProvider = null,
        AlarmCoordinator? alarmCoordinator = null,
        IAuthService? authService = null)
    {
        _permissionService = permissionService ?? throw new ArgumentNullException(nameof(permissionService));
        _holidayService = holidayService ?? throw new ArgumentNullException(nameof(holidayService));
        _deviceInfoProvider = deviceInfoProvider ?? new LocalDeviceInfoProvider();
        _alarmCoordinator = alarmCoordinator;
        _authService = authService;

        if (_authService != null)
        {
            _authService.StateChanged += (_, _) => OnPropertyChanged(nameof(AuthStatus));
        }

        RequestNotificationCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                _permissionService.RequestNotificationPermission();
                await Task.Yield();
                UpdateProperties();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && CanRequestNotification);

        OpenExactAlarmSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                _permissionService.OpenExactAlarmSettings();
                await Task.Yield();
                UpdateProperties();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && CanOpenExactAlarmSettings);

        OpenFullScreenSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                _permissionService.OpenFullScreenIntentSettings();
                await Task.Yield();
                UpdateProperties();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && CanOpenFullScreenSettings);

        OpenBatteryOptimizationSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                _permissionService.OpenBatteryOptimizationSettings();
                await Task.Yield();
                UpdateProperties();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && CanOpenBatteryOptimizationSettings);

        RefreshCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                UpdateProperties();

                if (_alarmCoordinator is not null)
                {
                    var count = await _alarmCoordinator.RescheduleAllActiveAsync();
                    RescheduledMessage = $"{count} 件の有効アラームを再評価・再登録しました。";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy);

        RefreshHolidaysCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            IsBusy = true;
            HolidayOperationMessage = "祝日データを取得中...";
            try
            {
                var success = await _holidayService.RefreshHolidaysAsync();
                if (success)
                {
                    if (_alarmCoordinator is not null)
                    {
                        var count = await _alarmCoordinator.RescheduleAllActiveAsync();
                        HolidayOperationMessage = $"祝日データを更新し、{count} 件のアラームを再計算・再登録しました。";
                    }
                    else
                    {
                        HolidayOperationMessage = "祝日データを更新しました。";
                    }
                }
                else
                {
                    HolidayOperationMessage = _holidayService.DataWarning ?? "祝日データの更新に失敗しました（既存データ維持）。";
                }
                UpdateProperties();
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy);
    }

    private void UpdateProperties()
    {
        OnPropertyChanged(string.Empty);
        RequestNotificationCommand.RaiseCanExecuteChanged();
        OpenExactAlarmSettingsCommand.RaiseCanExecuteChanged();
        OpenFullScreenSettingsCommand.RaiseCanExecuteChanged();
        OpenBatteryOptimizationSettingsCommand.RaiseCanExecuteChanged();
    }
}
