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

    private bool _isBusy;
    private bool _isRingingSubscribed;
    private string? _statusMessage;
    private LocalAlarmModel? _selectedAlarm;

    public ObservableCollection<LocalAlarmModel> Alarms { get; } = new();
    public ObservableCollection<ActiveRingingAlarm> RingingAlarms { get; } = new();

    public bool IsRinging => RingingAlarms.Count > 0;

    public string RingingBannerTitle => RingingAlarms.Count switch
    {
        0 => string.Empty,
        1 => $"{RingingAlarms[0].AlarmName} が鳴動中",
        _ => $"{RingingAlarms.Count} 件のアラームが同時鳴動中"
    };

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

    public string HolidayStatusText => _holidayService.HasHolidayData
        ? "日本の祝日データ適用済み (祝日自動スキップ有効)"
        : (_holidayService.DataWarning ?? "祝日データ未取得 (鳴動優先)");

    public string HolidayNotice => HolidayStatusText;

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
        IMainThreadDispatcher? dispatcher = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _holidayService = holidayService ?? throw new ArgumentNullException(nameof(holidayService));
        _deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
        _ringingService = ringingService;
        _holidaySyncCoordinator = holidaySyncCoordinator;
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();

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
        }, _ => !IsBusy);

        DismissAllRingingCommand = new AsyncRelayCommand(async _ =>
        {
            await DismissAllRingingAsync();
        }, _ => !IsBusy && IsRinging);

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
        }
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
