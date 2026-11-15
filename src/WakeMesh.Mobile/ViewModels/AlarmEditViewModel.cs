using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.ViewModels;

public sealed class AlarmEditViewModel : ViewModelBase
{
    private readonly AlarmCoordinator _coordinator;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly IAudioPlaybackService? _audioPlaybackService;
    private readonly IGroupAlarmSource? _groupSource;

    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "アラーム";
    private TimeOnly _time = new(7, 0);
    private bool _isEnabled = true;
    private bool _isWeekly = true;
    private DateTime _onceDate = DateTime.Today;
    private Weekdays _activeWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday;
    private bool _excludeHolidays = true;
    private int _snoozeMinutes = 5;
    private int _volumePercent = 100;
    private bool _enableVibration = true;
    private SoundOption _selectedSound = SoundOption.OsDefault;
    private bool _isPlayingPreview;
    private string _timeZoneId = "Asia/Tokyo";
    private bool _isBusy;
    private string? _errorMessage;
    private bool _isEditMode;
    private bool _isEditRefused;

    public string PageTitle => _isEditMode ? "アラームの編集" : "アラームの新規作成";

    public Action? OnSaveSuccess { get; set; }

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public TimeOnly Time
    {
        get => _time;
        set
        {
            if (SetProperty(ref _time, value))
            {
                OnPropertyChanged(nameof(TimeOfDay));
                SuggestOnceDateIfNeeded();
            }
        }
    }

    public TimeSpan TimeOfDay
    {
        get => _time.ToTimeSpan();
        set => Time = TimeOnly.FromTimeSpan(value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsWeekly
    {
        get => _isWeekly;
        set
        {
            if (SetProperty(ref _isWeekly, value))
            {
                OnPropertyChanged(nameof(IsOnce));
                SuggestOnceDateIfNeeded();
            }
        }
    }

    public bool IsOnce
    {
        get => !_isWeekly;
        set => IsWeekly = !value;
    }

    public DateTime OnceDate
    {
        get => _onceDate;
        set => SetProperty(ref _onceDate, value);
    }

    public Weekdays ActiveWeekdays
    {
        get => _activeWeekdays;
        set
        {
            if (SetProperty(ref _activeWeekdays, value))
            {
                OnPropertyChanged(nameof(IsMonday));
                OnPropertyChanged(nameof(IsTuesday));
                OnPropertyChanged(nameof(IsWednesday));
                OnPropertyChanged(nameof(IsThursday));
                OnPropertyChanged(nameof(IsFriday));
                OnPropertyChanged(nameof(IsSaturday));
                OnPropertyChanged(nameof(IsSunday));
                OnPropertyChanged(nameof(RepeatMonday));
                OnPropertyChanged(nameof(RepeatTuesday));
                OnPropertyChanged(nameof(RepeatWednesday));
                OnPropertyChanged(nameof(RepeatThursday));
                OnPropertyChanged(nameof(RepeatFriday));
                OnPropertyChanged(nameof(RepeatSaturday));
                OnPropertyChanged(nameof(RepeatSunday));
            }
        }
    }

    public bool IsMonday
    {
        get => (_activeWeekdays & Weekdays.Monday) != 0;
        set => SetWeekday(Weekdays.Monday, value);
    }

    public bool IsTuesday
    {
        get => (_activeWeekdays & Weekdays.Tuesday) != 0;
        set => SetWeekday(Weekdays.Tuesday, value);
    }

    public bool IsWednesday
    {
        get => (_activeWeekdays & Weekdays.Wednesday) != 0;
        set => SetWeekday(Weekdays.Wednesday, value);
    }

    public bool IsThursday
    {
        get => (_activeWeekdays & Weekdays.Thursday) != 0;
        set => SetWeekday(Weekdays.Thursday, value);
    }

    public bool IsFriday
    {
        get => (_activeWeekdays & Weekdays.Friday) != 0;
        set => SetWeekday(Weekdays.Friday, value);
    }

    public bool IsSaturday
    {
        get => (_activeWeekdays & Weekdays.Saturday) != 0;
        set => SetWeekday(Weekdays.Saturday, value);
    }

    public bool IsSunday
    {
        get => (_activeWeekdays & Weekdays.Sunday) != 0;
        set => SetWeekday(Weekdays.Sunday, value);
    }

    public bool RepeatMonday { get => IsMonday; set => IsMonday = value; }
    public bool RepeatTuesday { get => IsTuesday; set => IsTuesday = value; }
    public bool RepeatWednesday { get => IsWednesday; set => IsWednesday = value; }
    public bool RepeatThursday { get => IsThursday; set => IsThursday = value; }
    public bool RepeatFriday { get => IsFriday; set => IsFriday = value; }
    public bool RepeatSaturday { get => IsSaturday; set => IsSaturday = value; }
    public bool RepeatSunday { get => IsSunday; set => IsSunday = value; }

    public bool ExcludeHolidays
    {
        get => _excludeHolidays;
        set
        {
            if (SetProperty(ref _excludeHolidays, value))
            {
                OnPropertyChanged(nameof(SkipHolidays));
            }
        }
    }

    public bool SkipHolidays
    {
        get => ExcludeHolidays;
        set => ExcludeHolidays = value;
    }

    public int SnoozeMinutes
    {
        get => _snoozeMinutes;
        set => SetProperty(ref _snoozeMinutes, value);
    }

    public int VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (SetProperty(ref _volumePercent, value))
            {
                if (IsPlayingPreview && _audioPlaybackService != null)
                {
                    _ = _audioPlaybackService.PlayPreviewAsync(SelectedSound.Uri, _volumePercent);
                }
            }
        }
    }

    public bool EnableVibration
    {
        get => _enableVibration;
        set => SetProperty(ref _enableVibration, value);
    }

    public IReadOnlyList<SoundOption> SoundOptions => SoundOption.AvailableOptions;

    public SoundOption SelectedSound
    {
        get => _selectedSound;
        set
        {
            if (SetProperty(ref _selectedSound, value ?? SoundOption.OsDefault))
            {
                if (IsPlayingPreview && _audioPlaybackService != null)
                {
                    _ = _audioPlaybackService.PlayPreviewAsync(_selectedSound.Uri, VolumePercent);
                }
            }
        }
    }

    public bool IsPlayingPreview
    {
        get => _isPlayingPreview;
        set
        {
            if (SetProperty(ref _isPlayingPreview, value))
            {
                OnPropertyChanged(nameof(PreviewButtonText));
            }
        }
    }

    public string PreviewButtonText => IsPlayingPreview ? "停止" : "試聴";

    public string TimeZoneId
    {
        get => _timeZoneId;
        set => SetProperty(ref _timeZoneId, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                TogglePreviewSoundCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True when this editor was opened for a cloud-owned alarm and must not save anything.</summary>
    public bool IsEditRefused => _isEditRefused;

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand TogglePreviewSoundCommand { get; }

    public AlarmEditViewModel(
        AlarmCoordinator coordinator,
        IDeviceInfoProvider deviceInfoProvider,
        IAudioPlaybackService? audioPlaybackService = null,
        IGroupAlarmSource? groupSource = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
        _audioPlaybackService = audioPlaybackService;
        _groupSource = groupSource;

        SaveCommand = new AsyncRelayCommand(async _ => await SaveAlarmAsync(), _ => !IsBusy);
        TogglePreviewSoundCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsPlayingPreview)
            {
                if (_audioPlaybackService != null)
                {
                    await _audioPlaybackService.StopPreviewAsync();
                }
                IsPlayingPreview = false;
            }
            else
            {
                if (_audioPlaybackService != null)
                {
                    await _audioPlaybackService.PlayPreviewAsync(SelectedSound.Uri, VolumePercent);
                    IsPlayingPreview = true;
                }
            }
        }, _ => !IsBusy);
    }

    public void LoadAlarm(LocalAlarmModel? alarm)
    {
        if (alarm is null)
        {
            _isEditMode = false;
            _isEditRefused = false;
            Id = Guid.NewGuid().ToString("N");
            Name = "新規アラーム";
            Time = new TimeOnly(7, 0);
            IsEnabled = true;
            IsWeekly = true;
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday;
            ExcludeHolidays = true;
            SnoozeMinutes = 5;
            VolumePercent = 100;
            EnableVibration = true;
            SelectedSound = SoundOption.OsDefault;
            IsPlayingPreview = false;
            TimeZoneId = "Asia/Tokyo";
            ErrorMessage = null;
            SuggestOnceDateIfNeeded();
        }
        else
        {
            _isEditMode = true;
            LoadFrom(alarm);
        }
        OnPropertyChanged(nameof(PageTitle));
    }

    public void LoadFrom(LocalAlarmModel alarm)
    {
        if (alarm is null)
        {
            return;
        }

        if (IsCloudOwnedId(alarm.Id))
        {
            // T113 §3: a cloud (group) alarm must never be edited through the manual editor.
            _isEditMode = false;
            _isEditRefused = true;
            Id = alarm.Id;   // remembered so a later save is refused for the same alarm, never re-created
            ErrorMessage = NotEditableMessage;
            return;
        }

        _isEditMode = true;
        _isEditRefused = false;
        Id = alarm.Id;
        Name = alarm.Name;
        Time = alarm.Time;
        IsEnabled = alarm.IsEnabled;
        IsWeekly = alarm.RepeatKind == AlarmRepeatKind.Weekly;
        ActiveWeekdays = alarm.ActiveWeekdays;
        OnceDate = alarm.OnceOnDate.HasValue
            ? alarm.OnceOnDate.Value.ToDateTime(TimeOnly.MinValue)
            : DateTime.Today;
        ExcludeHolidays = alarm.ExcludeHolidays;
        SnoozeMinutes = alarm.SnoozeMinutes;
        VolumePercent = alarm.VolumePercent;
        EnableVibration = alarm.EnableVibration;
        SelectedSound = SoundOption.FromUri(alarm.SoundUri);
        IsPlayingPreview = false;
        TimeZoneId = string.IsNullOrWhiteSpace(alarm.TimeZoneId) ? "Asia/Tokyo" : alarm.TimeZoneId;
        ErrorMessage = null;
        OnPropertyChanged(nameof(PageTitle));
    }

    public async Task<bool> SaveAlarmAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            // T113 §3: never save a cloud-owned alarm through the manual editor — not even a legacy
            // non-prefixed id that the current binding's manifest maps to a schedule.
            if (_isEditRefused || IsCloudOwnedId(Id) || await IsManifestManagedAsync(Id).ConfigureAwait(false))
            {
                ErrorMessage = NotEditableMessage;
                return false;
            }

            if (IsPlayingPreview && _audioPlaybackService != null)
            {
                await _audioPlaybackService.StopPreviewAsync();
                IsPlayingPreview = false;
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                ErrorMessage = "アラーム名を入力してください。";
                return false;
            }

            if (IsWeekly && ActiveWeekdays == Weekdays.None)
            {
                ErrorMessage = "繰り返しアラームでは少なくとも1つの曜日を選択してください。";
                return false;
            }

            DateOnly? onceOnDate = null;
            if (!IsWeekly)
            {
                onceOnDate = DateOnly.FromDateTime(OnceDate);
                // Ensure target time is in the future if date is today
                var now = DateTime.Now;
                var targetDateTime = onceOnDate.Value.ToDateTime(Time);
                if (targetDateTime <= now)
                {
                    // Item 6: If today is already past, explicitly select tomorrow
                    onceOnDate = DateOnly.FromDateTime(now.AddDays(1));
                    OnceDate = onceOnDate.Value.ToDateTime(TimeOnly.MinValue);
                }
            }

            // Check if existing alarm in store has active snooze to preserve unless user explicitly disabled (Requirement 4)
            DateTimeOffset? preservedSnooze = null;
            var existing = await _coordinator.GetByIdAsync(Id);
            if (existing is not null && IsEnabled)
            {
                preservedSnooze = existing.SnoozeUntil;
            }

            var model = new LocalAlarmModel
            {
                Id = Id,
                Name = Name.Trim(),
                Time = Time,
                IsEnabled = IsEnabled,
                RepeatKind = IsWeekly ? AlarmRepeatKind.Weekly : AlarmRepeatKind.Once,
                ActiveWeekdays = IsWeekly ? ActiveWeekdays : Weekdays.None,
                OnceOnDate = onceOnDate,
                ExcludeHolidays = ExcludeHolidays,
                SnoozeMinutes = SnoozeMinutes,
                VolumePercent = VolumePercent,
                EnableVibration = EnableVibration,
                SoundUri = SelectedSound.Uri,
                DeviceId = _deviceInfoProvider.DeviceId,
                TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? "Asia/Tokyo" : TimeZoneId,
                SnoozeUntil = preservedSnooze
            };

            await _coordinator.SaveAlarmAsync(model);
            OnSaveSuccess?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存に失敗しました: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SuggestOnceDateIfNeeded()
    {
        if (!IsWeekly)
        {
            var now = DateTime.Now;
            var todayTarget = DateTime.Today.Add(Time.ToTimeSpan());
            if (todayTarget <= now)
            {
                OnceDate = DateTime.Today.AddDays(1);
            }
            else
            {
                OnceDate = DateTime.Today;
            }
        }
    }

    private void SetWeekday(Weekdays day, bool enabled)
    {
        if (enabled)
        {
            ActiveWeekdays |= day;
        }
        else
        {
            ActiveWeekdays &= ~day;
        }
    }

    public const string NotEditableMessage =
        "このアラームはここでは変更できません。クラウドの画面のアラームグループから変更してください。";

    /// <summary>Cloud-owned ids always start with the sync prefix; manual ids never do.</summary>
    private static bool IsCloudOwnedId(string? id) =>
        !string.IsNullOrEmpty(id) &&
        id.StartsWith(SyncLocalAlarmIdentity.IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Legacy cloud alarms created by older builds can carry a non-prefixed id that is only recorded in the
    /// manifest, so the save path asks the read-only source before writing. Without a source the prefix
    /// check above is the whole contract (and the existing manual-alarm tests keep their behaviour).
    /// </summary>
    private async Task<bool> IsManifestManagedAsync(string? id)
    {
        if (string.IsNullOrEmpty(id) || _groupSource is null)
        {
            return false;
        }

        try
        {
            var result = await _groupSource.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            return GroupAlarmProjector.IsCloudManagedId(id, result.Binding, result.Manifest);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
