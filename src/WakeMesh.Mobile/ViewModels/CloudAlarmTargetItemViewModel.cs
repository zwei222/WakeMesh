using System;
using WakeMesh.Client.Models;

namespace WakeMesh.Mobile.ViewModels;

public sealed class CloudAlarmTargetItemViewModel : ViewModelBase
{
    private Guid? _id;
    private Guid _deviceId;
    private string _deviceDisplayName;
    private TimeSpan _timeOfDay;
    private string _soundKey;
    private int _volume;
    private bool _vibrate;
    private bool _enabled;
    private string _reflectionStatusText = "未同期";
    private string _reflectionStateKey = "unknown";

    public Guid? Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public Guid DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }

    public string DeviceDisplayName
    {
        get => _deviceDisplayName;
        set => SetProperty(ref _deviceDisplayName, value);
    }

    public TimeSpan TimeOfDay
    {
        get => _timeOfDay;
        set
        {
            if (SetProperty(ref _timeOfDay, value))
            {
                OnPropertyChanged(nameof(TimeString));
            }
        }
    }

    public string TimeString => $"{TimeOfDay.Hours:D2}:{TimeOfDay.Minutes:D2}";

    public string SoundKey
    {
        get => _soundKey;
        set => SetProperty(ref _soundKey, value);
    }

    public int Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, value);
    }

    public bool Vibrate
    {
        get => _vibrate;
        set => SetProperty(ref _vibrate, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string ReflectionStatusText
    {
        get => _reflectionStatusText;
        set => SetProperty(ref _reflectionStatusText, value);
    }

    /// <summary>
    /// T80 P0-9: mark key for this target's device, using the same exact values as
    /// <see cref="CloudDeviceItemViewModel.StateKey"/> (ok / pending / unknown / warn / critical /
    /// inactive). "unknown" means the device projection was not available, so no success is claimed.
    /// </summary>
    public string ReflectionStateKey
    {
        get => _reflectionStateKey;
        set => SetProperty(ref _reflectionStateKey, value);
    }

    public CloudAlarmTargetItemViewModel(
        Guid deviceId,
        string deviceDisplayName,
        TimeSpan timeOfDay,
        string soundKey = "default",
        int volume = 80,
        bool vibrate = true,
        bool enabled = true,
        Guid? id = null)
    {
        _deviceId = deviceId;
        _deviceDisplayName = deviceDisplayName;
        _timeOfDay = timeOfDay;
        _soundKey = soundKey;
        _volume = volume;
        _vibrate = vibrate;
        _enabled = enabled;
        _id = id;
    }

    public static CloudAlarmTargetItemViewModel FromDto(AlarmTargetDto dto, string? deviceDisplayName = null)
    {
        var timeSpan = TimeSpan.FromHours(7);
        if (TimeSpan.TryParse(dto.Time, out var parsed))
        {
            timeSpan = parsed;
        }

        return new CloudAlarmTargetItemViewModel(
            dto.DeviceId,
            deviceDisplayName ?? $"端末 {dto.DeviceId:N}"[..12],
            timeSpan,
            dto.SoundKey,
            dto.Volume,
            dto.Vibrate,
            dto.Enabled,
            dto.Id);
    }

    public AlarmTargetDto ToDto()
    {
        return new AlarmTargetDto(
            Id,
            DeviceId,
            TimeString,
            SoundKey,
            Volume,
            Vibrate,
            Enabled);
    }
}
