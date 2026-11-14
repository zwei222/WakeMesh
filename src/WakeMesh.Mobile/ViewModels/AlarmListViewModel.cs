using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.ViewModels;

public sealed class AlarmListViewModel : ViewModelBase, IDisposable
{
    private readonly AlarmCoordinator _coordinator;
    private readonly IHolidayService _holidayService;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly IAlarmRingingService? _ringingService;
    private readonly HolidayForegroundSyncCoordinator? _holidaySyncCoordinator;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IPermissionService? _permissionService;

    private bool _isBusy;
    private bool _isRingingSubscribed;
    private string? _statusMessage;
    private LocalAlarmModel? _selectedAlarm;
    private string _readinessLevel = "None";
    private string _readinessText = string.Empty;

    public ObservableCollection<LocalAlarmModel> Alarms { get; } = new();
    public ObservableCollection<ActiveRingingAlarm> RingingAlarms { get; } = new();

    public bool IsRinging => RingingAlarms.Count > 0;

    public string RingingBannerTitle => RingingAlarms.Count switch
    {
        0 => string.Empty,
        1 => $"{RingingAlarms[0].AlarmName} のアラームが鳴っています",
        _ => $"{RingingAlarms.Count} 件のアラームが同時に鳴っています"
    };

    /// <summary>
    /// T80 P0-8: the alarm tab states whether this device can actually ring, without repeating the
    /// diagnostics page. "None" means every permission this OS level requires is known and granted;
    /// "Critical" means exact alarms are needed but not granted; "Warn" covers the remaining gaps.
    /// </summary>
    public string ReadinessLevel => _readinessLevel;

    /// <summary>Plain-language list of what is missing. Empty when <see cref="ReadinessLevel"/> is None.</summary>
    public string ReadinessText => _readinessText;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                LoadAlarmsCommand.RaiseCanExecuteChanged();
                ToggleAlarmCommand.RaiseCanExecuteChanged();
                DeleteAlarmCommand.RaiseCanExecuteChanged();
                DismissRingingAlarmCommand.RaiseCanExecuteChanged();
                DismissAllRingingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public LocalAlarmModel? SelectedAlarm
    {
        get => _selectedAlarm;
        set => SetProperty(ref _selectedAlarm, value);
    }

    // 正直なステータス表示（偽装しない）
    public string CloudSyncStatusText => "未接続 (ローカル動作中)";
    public string AuthStatusText => "未サインイン";
    public string DeviceDisplayName => _deviceInfoProvider.DeviceDisplayName;
    public string DeviceId => _deviceInfoProvider.DeviceId;

    // T88 copy-map: the row states the fact in plain words. The service's internal warning text
    // ("祝日データ未取得 (鳴動優先)") stays on the settings page as the refresh detail, not here.
    public string HolidayStatusText => _holidayService.HasHolidayData
        ? "祝日情報を取得済み（祝日はアラームが鳴りません）"
        : "祝日情報が未取得です（祝日もアラーム対象）";

    public string HolidayNotice => HolidayStatusText;

    /// <summary>T80 P1-2: true while the holiday calendar is unknown, so holidays still ring.</summary>
    public bool IsHolidayDataMissing => !_holidayService.HasHolidayData;

    public AsyncRelayCommand LoadAlarmsCommand { get; }
    public AsyncRelayCommand ToggleAlarmCommand { get; }
    public AsyncRelayCommand DeleteAlarmCommand { get; }
    public AsyncRelayCommand DismissRingingAlarmCommand { get; }
    public AsyncRelayCommand DismissAllRingingCommand { get; }

    public AlarmListViewModel(
        AlarmCoordinator coordinator,
        IHolidayService holidayService,
        IDeviceInfoProvider deviceInfoProvider,
        IAlarmRingingService? ringingService = null,
        HolidayForegroundSyncCoordinator? holidaySyncCoordinator = null,
        IMainThreadDispatcher? dispatcher = null,
        IPermissionService? permissionService = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _holidayService = holidayService ?? throw new ArgumentNullException(nameof(holidayService));
        _deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
        _ringingService = ringingService;
        _holidaySyncCoordinator = holidaySyncCoordinator;
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();
        _permissionService = permissionService;

        LoadAlarmsCommand = new AsyncRelayCommand(async _ => await LoadAlarmsAsync(), _ => !IsBusy);
        ToggleAlarmCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string id)
            {
                await ToggleAlarmAsync(id);
            }
            else if (param is LocalAlarmModel alarm)
            {
                await ToggleAlarmAsync(alarm);
            }
        }, _ => !IsBusy);
        DeleteAlarmCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string id)
            {
                await DeleteAlarmAsync(id);
            }
            else if (param is LocalAlarmModel alarm)
            {
                await DeleteAlarmAsync(alarm);
            }
        }, _ => !IsBusy);

        DismissRingingAlarmCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string id)
            {
                await DismissRingingAlarmAsync(id);
            }
            else if (param is ActiveRingingAlarm ringing)
            {
                await DismissRingingAlarmAsync(ringing.AlarmId);
            }
        }, _ => true); // T80 P0-5: stopping must not wait for the list reload

        DismissAllRingingCommand = new AsyncRelayCommand(async _ =>
        {
            await DismissAllRingingAsync();
        }, _ => IsRinging); // T80 P0-5: stopping must not wait for the list reload

        RefreshReadiness();
        SubscribeRingingEvents();
    }

    public void SubscribeRingingEvents()
    {
        if (_ringingService == null || _isRingingSubscribed) return;
        _ringingService.RingingStateChanged += OnRingingStateChanged;
        _isRingingSubscribed = true;
        DispatchSyncRingingAlarms();
    }

    public void UnsubscribeRingingEvents()
    {
        if (_ringingService == null || !_isRingingSubscribed) return;
        _ringingService.RingingStateChanged -= OnRingingStateChanged;
        _isRingingSubscribed = false;
    }

    private void OnRingingStateChanged(object? sender, EventArgs e)
    {
        DispatchSyncRingingAlarms();
    }

    private void DispatchSyncRingingAlarms()
    {
        if (_dispatcher.IsDispatchRequired)
        {
            _dispatcher.Post(SyncRingingAlarms);
        }
        else
        {
            SyncRingingAlarms();
        }
    }

    public void Dispose()
    {
        UnsubscribeRingingEvents();
    }

    private void SyncRingingAlarms()
    {
        if (_ringingService == null) return;

        var active = _ringingService.GetRingingAlarms();
        RingingAlarms.Clear();
        foreach (var item in active)
        {
            RingingAlarms.Add(item);
        }

        OnPropertyChanged(nameof(IsRinging));
        OnPropertyChanged(nameof(RingingBannerTitle));
        DismissAllRingingCommand.RaiseCanExecuteChanged();
    }

    public async Task<bool> DismissRingingAlarmAsync(string alarmId)
    {
        if (_ringingService == null || string.IsNullOrWhiteSpace(alarmId)) return false;

        var success = await _ringingService.DismissAlarmAsync(alarmId);
        SyncRingingAlarms();
        return success;
    }

    public async Task<bool> DismissAllRingingAsync()
    {
        if (_ringingService == null) return false;

        var success = await _ringingService.DismissAllAsync();
        SyncRingingAlarms();
        return success;
    }

    public async Task LoadAlarmsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "アラームを読み込み中...";

        try
        {
            await Task.Yield();
            var count = await _coordinator.RescheduleAllActiveAsync();
            Alarms.Clear();
            var list = await _coordinator.GetAllAsync();
            foreach (var item in list)
            {
                Alarms.Add(item);
            }
            StatusMessage = $"{Alarms.Count} 件のアラーム ({count} 件有効)";
            SyncRingingAlarms();
            _holidaySyncCoordinator?.RequestForegroundSync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"読込失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshReadiness();
        }
    }

    /// <summary>
    /// T80 P0-8 / P1-2: re-reads the OS permission state. With no permission service the readiness is
    /// reported as unverified ("Warn") — it must never claim every permission is granted.
    /// </summary>
    public void RefreshReadiness()
    {
        var (level, text) = EvaluateReadiness();

        if (!string.Equals(_readinessLevel, level, StringComparison.Ordinal))
        {
            _readinessLevel = level;
            OnPropertyChanged(nameof(ReadinessLevel));
        }

        if (!string.Equals(_readinessText, text, StringComparison.Ordinal))
        {
            _readinessText = text;
            OnPropertyChanged(nameof(ReadinessText));
        }

        OnPropertyChanged(nameof(IsHolidayDataMissing));
    }

    private (string Level, string Text) EvaluateReadiness()
    {
        var permissions = _permissionService;
        if (permissions is null)
        {
            return ("Warn", "この端末の権限の状態を確認できませんでした。設定から確認してください。");
        }

        var api = permissions.OsApiLevel;
        if (api >= 31 && !permissions.CanScheduleExactAlarms)
        {
            return ("Critical", "「正確なアラーム」が許可されていません。");
        }

        var missing = new System.Collections.Generic.List<string>();
        if (api >= 33 && !permissions.HasNotificationPermission) missing.Add("通知権限");
        if (api >= 34 && !permissions.CanUseFullScreenIntent) missing.Add("全画面表示");
        if (!permissions.IsIgnoringBatteryOptimizations) missing.Add("電池最適化の除外");

        if (missing.Count == 0)
        {
            return ("None", string.Empty);
        }

        return ("Warn", $"アラームを鳴らすには、次の設定を確認してください。{string.Join("、", missing)}。");
    }

    public async Task<bool> ToggleAlarmAsync(LocalAlarmModel alarm)
    {
        if (alarm is null) return false;
        return await ToggleAlarmAsync(alarm.Id);
    }

    public async Task<bool> ToggleAlarmAsync(string id)
    {
        if (IsBusy) return false;
        IsBusy = true;

        try
        {
            var updated = await _coordinator.ToggleAlarmEnabledAsync(id);
            if (updated is not null)
            {
                var existing = Alarms.FirstOrDefault(a => a.Id == id);
                if (existing is not null)
                {
                    existing.IsEnabled = updated.IsEnabled;
                    existing.NextFireTime = updated.NextFireTime;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"切替エラー: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        return false;
    }

    /// <summary>
    /// T105: applies an explicit ON/OFF chosen by the user. The plain toggle on this page was an
    /// inverting API, so a Switch re-binding (false→true on first bind, cell reuse, reload) used to
    /// flip the saved value. The requested value is honoured against the *stored* value, and equal
    /// values are a no-op. The coordinator's inverting toggle is only used when the store disagrees.
    /// </summary>
    public async Task<bool> SetAlarmEnabledFromUserAsync(LocalAlarmModel alarm, bool requested)
    {
        if (alarm is null || alarm.IsEnabled == requested) return false;   // Binding が値を反映しただけ → no-op
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            var current = await _coordinator.GetByIdAsync(alarm.Id);
            if (current is null) return false;
            var result = current.IsEnabled == requested ? current
                       : await _coordinator.ToggleAlarmEnabledAsync(alarm.Id);   // 保存値が requested と違うときだけ反転
            if (result is null) return false;

            // T109 (T107 M1): LocalAlarmModel is a plain POCO with no INotifyPropertyChanged, so
            // writing IsEnabled/NextFireTime on the row object never re-renders it — an alarm switched
            // OFF kept showing "次回 …" and the stale enabled count (t106-native user_off).
            // Replace the item in the collection instead: ObservableCollection raises Replace, the row
            // re-binds to the saved values (no "次回" once disabled), and the re-bind feeds back the
            // saved value, which the equality no-op at the top of this method absorbs.
            var existing = Alarms.FirstOrDefault(a => a.Id == alarm.Id) ?? alarm;
            var updated = CopySavedState(result);
            var idx = Alarms.IndexOf(existing);
            if (idx >= 0)
            {
                Alarms[idx] = updated;
            }
            else
            {
                existing.IsEnabled = updated.IsEnabled;
                existing.NextFireTime = updated.NextFireTime;
            }

            // Same wording/format as LoadAlarmsAsync so the footer count cannot go stale after a switch.
            StatusMessage = $"{Alarms.Count} 件のアラーム ({Alarms.Count(a => a.IsEnabled)} 件有効)";
            return result.IsEnabled == requested;
        }
        catch (Exception ex)
        {
            StatusMessage = $"切替エラー: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// T109: builds a fresh row model holding every field of the just-saved alarm, so the collection
    /// can be replaced (and that one row redrawn) without JSON round-trips. The saved instance is
    /// authoritative for IsEnabled / NextFireTime / OS registration state; nothing is dropped.
    /// </summary>
    private static LocalAlarmModel CopySavedState(LocalAlarmModel saved) => new()
    {
        Id = saved.Id,
        Name = saved.Name,
        IsEnabled = saved.IsEnabled,
        Time = saved.Time,
        RepeatKind = saved.RepeatKind,
        ActiveWeekdays = saved.ActiveWeekdays,
        OnceOnDate = saved.OnceOnDate,
        ExcludeHolidays = saved.ExcludeHolidays,
        SnoozeMinutes = saved.SnoozeMinutes,
        SnoozeUntil = saved.SnoozeUntil,
        DeviceId = saved.DeviceId,
        TimeZoneId = saved.TimeZoneId,
        SyncStatus = saved.SyncStatus,
        OsRegistrationStatus = saved.OsRegistrationStatus,
        OsRegistrationError = saved.OsRegistrationError,
        VolumePercent = saved.VolumePercent,
        EnableVibration = saved.EnableVibration,
        SoundUri = saved.SoundUri,
        NextFireTime = saved.NextFireTime,
        HasHolidayCoverageWarning = saved.HasHolidayCoverageWarning
    };

    public async Task<bool> DeleteAlarmAsync(LocalAlarmModel alarm)
    {
        if (alarm is null) return false;
        return await DeleteAlarmAsync(alarm.Id);
    }

    public async Task<bool> DeleteAlarmAsync(string id)
    {
        if (IsBusy) return false;
        IsBusy = true;

        try
        {
            var success = await _coordinator.DeleteAlarmAsync(id);
            if (success)
            {
                var item = Alarms.FirstOrDefault(a => a.Id == id);
                if (item is not null)
                {
                    Alarms.Remove(item);
                }
            }
            return success;
        }
        catch (Exception ex)
        {
            StatusMessage = $"削除エラー: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        return false;
    }
}
