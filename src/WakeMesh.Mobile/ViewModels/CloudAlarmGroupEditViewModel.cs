using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.Services.CloudManagement;

namespace WakeMesh.Mobile.ViewModels;

public sealed class CloudAlarmGroupEditViewModel : ViewModelBase
{
    private readonly ICloudManagementService _cloudService;
    private readonly ICloudNavigation _navigation;

    private bool _isBusy;
    private bool _isNew = true;
    private string _groupId = string.Empty;
    private string _label = string.Empty;
    private bool _enabled = true;
    private string _repeatMode = "weekly"; // "weekly" or "once"
    private DateTime _onceDate = DateTime.Today;
    private string _timeZone = "Asia/Tokyo";
    private bool _excludeHolidays = true;
    private long _ifMatchVersion;
    private List<string> _excludedDates = new();

    // Day toggles
    private bool _dayMonday = true;
    private bool _dayTuesday = true;
    private bool _dayWednesday = true;
    private bool _dayThursday = true;
    private bool _dayFriday = true;
    private bool _daySaturday;
    private bool _daySunday;

    private bool _conflictDetected;
    private string? _conflictMessage;
    private string? _errorMessage;
    private string? _saveOutcomeNotice;

    private CloudDeviceItemViewModel? _selectedDeviceToAdd;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                ReloadFromServerCommand.RaiseCanExecuteChanged();
                AddTargetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNew
    {
        get => _isNew;
        private set => SetProperty(ref _isNew, value);
    }

    public string GroupId
    {
        get => _groupId;
        set => SetProperty(ref _groupId, value);
    }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (SetProperty(ref _repeatMode, value))
            {
                OnPropertyChanged(nameof(IsWeekly));
                OnPropertyChanged(nameof(IsOnce));
            }
        }
    }

    public bool IsWeekly => string.Equals(RepeatMode, "weekly", StringComparison.OrdinalIgnoreCase);
    public bool IsOnce => string.Equals(RepeatMode, "once", StringComparison.OrdinalIgnoreCase);

    public DateTime OnceDate
    {
        get => _onceDate;
        set => SetProperty(ref _onceDate, value);
    }

    public string TimeZone
    {
        get => _timeZone;
        set => SetProperty(ref _timeZone, value);
    }

    public bool ExcludeHolidays
    {
        get => _excludeHolidays;
        set => SetProperty(ref _excludeHolidays, value);
    }

    public long IfMatchVersion
    {
        get => _ifMatchVersion;
        private set => SetProperty(ref _ifMatchVersion, value);
    }

    public IReadOnlyList<string> ExcludedDates => _excludedDates;

    public bool DayMonday { get => _dayMonday; set => SetProperty(ref _dayMonday, value); }
    public bool DayTuesday { get => _dayTuesday; set => SetProperty(ref _dayTuesday, value); }
    public bool DayWednesday { get => _dayWednesday; set => SetProperty(ref _dayWednesday, value); }
    public bool DayThursday { get => _dayThursday; set => SetProperty(ref _dayThursday, value); }
    public bool DayFriday { get => _dayFriday; set => SetProperty(ref _dayFriday, value); }
    public bool DaySaturday { get => _daySaturday; set => SetProperty(ref _daySaturday, value); }
    public bool DaySunday { get => _daySunday; set => SetProperty(ref _daySunday, value); }

    public bool ConflictDetected
    {
        get => _conflictDetected;
        set => SetProperty(ref _conflictDetected, value);
    }

    public string? ConflictMessage
    {
        get => _conflictMessage;
        set => SetProperty(ref _conflictMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string? SaveOutcomeNotice
    {
        get => _saveOutcomeNotice;
        set => SetProperty(ref _saveOutcomeNotice, value);
    }

    public CloudDeviceItemViewModel? SelectedDeviceToAdd
    {
        get => _selectedDeviceToAdd;
        set
        {
            if (SetProperty(ref _selectedDeviceToAdd, value))
            {
                AddTargetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<CloudAlarmTargetItemViewModel> Targets { get; } = new();
    public ObservableCollection<CloudDeviceItemViewModel> AvailableDevices { get; } = new();

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand<CloudAlarmTargetItemViewModel> RemoveTargetCommand { get; }
    public AsyncRelayCommand ReloadFromServerCommand { get; }

    public CloudAlarmGroupEditViewModel(
        ICloudManagementService cloudService,
        ICloudNavigation? navigation = null)
    {
        _cloudService = cloudService ?? throw new ArgumentNullException(nameof(cloudService));
        _navigation = navigation ?? new MauiShellCloudNavigation();

        SaveCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy) return;
            ErrorMessage = null;
            ConflictDetected = false;
            ConflictMessage = null;

            if (string.IsNullOrWhiteSpace(Label))
            {
                ErrorMessage = "グループ名（ラベル）を入力してください。";
                return;
            }

            if (Targets.Count == 0)
            {
                ErrorMessage = "アラームを鳴らす端末を1台以上設定してください。";
                return;
            }

            List<int>? days = null;
            string? dateStr = null;

            if (IsWeekly)
            {
                // API/Client wire days: 0=Sunday .. 6=Saturday (matches DayOfWeek).
                days = new List<int>();
                if (DaySunday) days.Add(0);
                if (DayMonday) days.Add(1);
                if (DayTuesday) days.Add(2);
                if (DayWednesday) days.Add(3);
                if (DayThursday) days.Add(4);
                if (DayFriday) days.Add(5);
                if (DaySaturday) days.Add(6);

                if (days.Count == 0)
                {
                    ErrorMessage = "繰り返し曜日を少なくとも1日選択してください。";
                    return;
                }
            }
            else
            {
                dateStr = OnceDate.ToString("yyyy-MM-dd");
            }

            var targetDtos = Targets.Select(t => t.ToDto()).ToList();

            var request = new AlarmGroupWriteRequest(
                Id: IsNew ? (string.IsNullOrWhiteSpace(GroupId) ? null : GroupId.Trim()) : GroupId,
                Label: Label.Trim(),
                Enabled: Enabled,
                Repeat: RepeatMode,
                Days: days,
                Date: dateStr,
                TimeZone: string.IsNullOrWhiteSpace(TimeZone) ? "Asia/Tokyo" : TimeZone.Trim(),
                ExcludeHolidays: ExcludeHolidays,
                ExcludedDates: _excludedDates.ToArray(),
                Targets: targetDtos);

            IsBusy = true;
            try
            {
                if (IsNew)
                {
                    var result = await _cloudService.CreateAlarmGroupAsync(request).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        ErrorMessage = $"作成失敗: {result.Message}";
                        return;
                    }

                    var outcome = result.Value!;
                    IfMatchVersion = outcome.Group.Version;
                    GroupId = outcome.Group.Id;
                    IsNew = false;
                    SaveOutcomeNotice = $"サーバー保存成功 (v{outcome.Group.Version})。端末反映待ち: 各端末の同期完了をお待ちください。";
                }
                else
                {
                    var result = await _cloudService.ReplaceAlarmGroupAsync(GroupId, request, IfMatchVersion).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        if (result.StatusCode == 412 || result.Kind == WakeMesh.Client.ApiErrorKind.PreconditionFailed)
                        {
                            ConflictDetected = true;
                            ConflictMessage = "他の端末またはサーバーで更新されました（ETag 412競合）。入力内容は保持されています。最新情報を再読込して確認してください。";
                            return;
                        }

                        ErrorMessage = $"更新失敗: {result.Message}";
                        return;
                    }

                    var outcome = result.Value!;
                    IfMatchVersion = outcome.Group.Version;
                    SaveOutcomeNotice = $"サーバー保存成功 (v{outcome.Group.Version})。端末反映待ち: 各端末の同期完了をお待ちください。";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy);

        AddTargetCommand = new RelayCommand(_ =>
        {
            if (SelectedDeviceToAdd == null) return;
            var dev = SelectedDeviceToAdd;

            // Check if already in targets
            if (Targets.Any(t => t.DeviceId == dev.Id))
            {
                ErrorMessage = $"端末 '{dev.DisplayName}' は既に設定対象に含まれています。";
                return;
            }

            ErrorMessage = null;
            Targets.Add(new CloudAlarmTargetItemViewModel(
                dev.Id,
                dev.DisplayName,
                new TimeSpan(7, 0, 0),
                "default",
                80,
                true,
                true));
        }, _ => !IsBusy && SelectedDeviceToAdd != null);

        RemoveTargetCommand = new RelayCommand<CloudAlarmTargetItemViewModel>(target =>
        {
            if (target != null)
            {
                Targets.Remove(target);
            }
        });

        ReloadFromServerCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsBusy || IsNew || string.IsNullOrWhiteSpace(GroupId)) return;
            IsBusy = true;
            try
            {
                var groupsResult = await _cloudService.ListAlarmGroupsAsync().ConfigureAwait(false);
                if (groupsResult.IsSuccess && groupsResult.Value != null)
                {
                    var serverGroup = groupsResult.Value.FirstOrDefault(g => g.Id == GroupId);
                    if (serverGroup != null)
                    {
                        ApplyGroupDto(serverGroup);
                        ConflictDetected = false;
                        ConflictMessage = null;
                        ErrorMessage = null;
                        SaveOutcomeNotice = "最新のグループ設定を再読込しました。";
                    }
                    else
                    {
                        ErrorMessage = "サーバー上にこのグループが存在しません（削除された可能性があります）。";
                    }
                }
                else
                {
                    ErrorMessage = "最新情報の再読込に失敗しました。";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }, _ => !IsBusy && !IsNew);

        CancelCommand = new AsyncRelayCommand(async _ =>
        {
            await _navigation.GoToAsync("..").ConfigureAwait(false);
        }, _ => !IsBusy);
    }

    public CloudAlarmGroupEditViewModel(
        ICloudManagementService cloudService,
        Action<string>? navigationHandler)
        : this(cloudService, navigationHandler != null ? new ActionCloudNavigation(navigationHandler) : null)
    {
    }

    public async Task InitializeAsync(string? groupId = null)
    {
        IsBusy = true;
        try
        {
            // Load available devices
            var devResult = await _cloudService.ListDevicesAsync().ConfigureAwait(false);
            AvailableDevices.Clear();
            if (devResult.IsSuccess && devResult.Value != null)
            {
                var reg = await _cloudService.GetCurrentRegistrationAsync().ConfigureAwait(false);
                foreach (var d in devResult.Value)
                {
                    AvailableDevices.Add(new CloudDeviceItemViewModel(d, reg != null && d.Id == reg.ServerDeviceId));
                }

                if (AvailableDevices.Count > 0)
                {
                    SelectedDeviceToAdd = AvailableDevices[0];
                }
            }

            if (!string.IsNullOrWhiteSpace(groupId))
            {
                var groupsResult = await _cloudService.ListAlarmGroupsAsync().ConfigureAwait(false);
                if (groupsResult.IsSuccess && groupsResult.Value != null)
                {
                    var existing = groupsResult.Value.FirstOrDefault(g => g.Id == groupId);
                    if (existing != null)
                    {
                        ApplyGroupDto(existing);
                        return;
                    }
                }
            }

            // New group setup default
            IsNew = true;
            GroupId = $"group-{Guid.NewGuid():N}"[..18];
            Label = "朝のアラーム";
            Enabled = true;
            RepeatMode = "weekly";
            DayMonday = true;
            DayTuesday = true;
            DayWednesday = true;
            DayThursday = true;
            DayFriday = true;
            DaySaturday = false;
            DaySunday = false;
            TimeZone = "Asia/Tokyo";
            ExcludeHolidays = true;
            IfMatchVersion = 0;
            _excludedDates.Clear();
            OnPropertyChanged(nameof(ExcludedDates));

            // Pre-populate with current registered device if available
            var currentReg = await _cloudService.GetCurrentRegistrationAsync().ConfigureAwait(false);
            if (currentReg != null && Targets.Count == 0)
            {
                Targets.Add(new CloudAlarmTargetItemViewModel(
                    currentReg.ServerDeviceId,
                    string.IsNullOrWhiteSpace(currentReg.DeviceDisplayName) ? "この端末" : currentReg.DeviceDisplayName,
                    new TimeSpan(7, 0, 0),
                    "default",
                    80,
                    true,
                    true));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddExcludedDate(string date)
    {
        if (!string.IsNullOrWhiteSpace(date) && !_excludedDates.Contains(date))
        {
            _excludedDates.Add(date);
            OnPropertyChanged(nameof(ExcludedDates));
        }
    }

    public void RemoveExcludedDate(string date)
    {
        if (_excludedDates.Remove(date))
        {
            OnPropertyChanged(nameof(ExcludedDates));
        }
    }

    public void ApplyGroupDto(AlarmGroupDto dto)
    {
        IsNew = false;
        GroupId = dto.Id;
        Label = dto.Label;
        Enabled = dto.Enabled;
        IfMatchVersion = dto.Version;
        RepeatMode = string.Equals(dto.Repeat, "once", StringComparison.OrdinalIgnoreCase) ? "once" : "weekly";
        TimeZone = string.IsNullOrWhiteSpace(dto.TimeZone) ? "Asia/Tokyo" : dto.TimeZone;
        ExcludeHolidays = dto.ExcludeHolidays;

        _excludedDates = dto.ExcludedDates != null ? new List<string>(dto.ExcludedDates) : new List<string>();
        OnPropertyChanged(nameof(ExcludedDates));

        if (DateTime.TryParse(dto.Date, out var parsedDate))
        {
            OnceDate = parsedDate;
        }

        // API/Client wire days: 0=Sunday .. 6=Saturday.
        var days = dto.Days?.ToHashSet() ?? new HashSet<int>();
        DaySunday = days.Contains(0);
        DayMonday = days.Contains(1);
        DayTuesday = days.Contains(2);
        DayWednesday = days.Contains(3);
        DayThursday = days.Contains(4);
        DayFriday = days.Contains(5);
        DaySaturday = days.Contains(6);

        Targets.Clear();
        if (dto.Targets != null)
        {
            foreach (var t in dto.Targets)
            {
                var devName = AvailableDevices.FirstOrDefault(d => d.Id == t.DeviceId)?.DisplayName;
                Targets.Add(CloudAlarmTargetItemViewModel.FromDto(t, devName));
            }
        }
    }
}
