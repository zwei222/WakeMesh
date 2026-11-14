using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public sealed class StubAlarmScheduler : IAlarmScheduler
{
    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime) => Task.CompletedTask;
    public Task CancelAlarmAsync(string alarmId) => Task.CompletedTask;
}

public sealed class StubPermissionService : IPermissionService
{
    public int OsApiLevel => 35;
    public bool CanScheduleExactAlarms => true;
    public bool HasNotificationPermission => true;
    public bool IsIgnoringBatteryOptimizations => true;
    public bool CanUseFullScreenIntent => true;

    public void RequestNotificationPermission() { }
    public void OpenExactAlarmSettings() { }
    public void OpenFullScreenIntentSettings() { }
    public void OpenBatteryOptimizationSettings() { }
}

public sealed class StubAlarmRingingService : IAlarmRingingService
{
    private readonly List<ActiveRingingAlarm> _ringing = new();

    public bool HasActiveAlarms => _ringing.Count > 0;

    public IReadOnlyList<ActiveRingingAlarm> GetRingingAlarms() => _ringing.ToList();

    public event EventHandler? RingingStateChanged;

    public void AddForTesting(ActiveRingingAlarm alarm)
    {
        _ringing.Add(alarm);
        RingingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<bool> DismissAlarmAsync(string alarmId)
    {
        var count = _ringing.RemoveAll(a => a.AlarmId == alarmId);
        if (count > 0)
        {
            RingingStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> DismissAllAsync()
    {
        if (_ringing.Count > 0)
        {
            _ringing.Clear();
            RingingStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}

public sealed class StubAudioPlaybackService : IAudioPlaybackService
{
    public bool IsPlaying { get; private set; }
    public string? LastPlayedUri { get; private set; }
    public int LastVolumePercent { get; private set; }

    public Task PlayPreviewAsync(string? soundUri, int volumePercent)
    {
        IsPlaying = true;
        LastPlayedUri = soundUri;
        LastVolumePercent = volumePercent;
        return Task.CompletedTask;
    }

    public Task StopPreviewAsync()
    {
        IsPlaying = false;
        return Task.CompletedTask;
    }
}

public sealed class StubSecureStorageProvider : ISecureStorageProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _storage = new();

    public Task<string?> GetAsync(string key)
    {
        _storage.TryGetValue(key, out var val);
        return Task.FromResult<string?>(val);
    }

    public Task SetAsync(string key, string value)
    {
        _storage[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key)
    {
        return Task.FromResult(_storage.TryRemove(key, out _));
    }

    public void RemoveAll()
    {
        _storage.Clear();
    }
}

public sealed class StubMsalClientAdapter : IMsalClientAdapter
{
    public Task<string?> AcquireTokenSilentAsync(string[] scopes, System.Threading.CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AcquireTokenInteractiveAsync(string[] scopes, System.Threading.CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task SignOutAsync(System.Threading.CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> HasAccountAsync(System.Threading.CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
