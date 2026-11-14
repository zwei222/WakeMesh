using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace WakeMesh.Mobile.Services;

public sealed record ActiveRingingAlarm(
    string AlarmId,
    string AlarmName,
    DateTimeOffset TriggerTime,
    int SnoozeMinutes,
    int VolumePercent = 100,
    bool EnableVibration = true,
    string? SoundUri = null);

public sealed class ActiveAlarmRingManager : IAlarmRingingService
{
    public static ActiveAlarmRingManager Shared { get; } = new();

    private readonly ConcurrentDictionary<string, ActiveRingingAlarm> _ringingAlarms = new();
    private readonly object _syncLock = new();

    public event EventHandler? RingingStateChanged;

    public static readonly TimeSpan MaxRingingDuration = TimeSpan.FromMinutes(10);

    private void RaiseRingingStateChanged()
    {
        var handler = RingingStateChanged;
        if (handler is null) return;

        foreach (var invocation in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)invocation)(this, EventArgs.Empty);
            }
            catch
            {
                // 購読者例外がtimeout停止や他処理を妨げないように隔離
            }
        }
    }

    /// <summary>
    /// Adds an alarm to the ringing set.
    /// Returns true if this is the first ringing alarm (meaning audio player must start).
    /// Returns false if other alarms are already ringing (meaning existing player continues).
    /// </summary>
    public bool AddRingingAlarm(ActiveRingingAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        bool wasEmpty;
        lock (_syncLock)
        {
            wasEmpty = _ringingAlarms.IsEmpty;
            _ringingAlarms[alarm.AlarmId] = alarm;
        }
        RaiseRingingStateChanged();
        return wasEmpty;
    }

    public IReadOnlyList<ActiveRingingAlarm> GetRingingAlarms()
    {
        lock (_syncLock)
        {
            return _ringingAlarms.Values.OrderBy(a => a.TriggerTime).ToList();
        }
    }

    /// <summary>
    /// Dismisses a single alarm.
    /// Returns true if other alarms are still ringing (keep player running).
    /// Returns false if no more alarms are ringing (stop player and service).
    /// </summary>
    public bool DismissAlarm(string alarmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmId);
        bool hasRemaining;
        lock (_syncLock)
        {
            _ringingAlarms.TryRemove(alarmId, out _);
            hasRemaining = !_ringingAlarms.IsEmpty;
        }
        RaiseRingingStateChanged();
        return hasRemaining;
    }

    /// <summary>
    /// Dismisses all ringing alarms.
    /// </summary>
    public void DismissAll()
    {
        lock (_syncLock)
        {
            _ringingAlarms.Clear();
        }
        RaiseRingingStateChanged();
    }

    public Task<bool> DismissAlarmAsync(string alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId)) return Task.FromResult(false);
        bool removed;
        lock (_syncLock)
        {
            removed = _ringingAlarms.TryRemove(alarmId, out _);
        }
        if (removed)
        {
            RaiseRingingStateChanged();
        }
        return Task.FromResult(removed);
    }

    public Task<bool> DismissAllAsync()
    {
        bool hadAlarms;
        lock (_syncLock)
        {
            hadAlarms = !_ringingAlarms.IsEmpty;
            _ringingAlarms.Clear();
        }
        if (hadAlarms)
        {
            RaiseRingingStateChanged();
        }
        return Task.FromResult(hadAlarms);
    }

    /// <summary>
    /// Snoozes an alarm and removes it from the ringing list.
    /// Returns the snoozed alarm info if found.
    /// </summary>
    public ActiveRingingAlarm? SnoozeAlarm(string alarmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmId);
        ActiveRingingAlarm? alarm;
        lock (_syncLock)
        {
            _ringingAlarms.TryRemove(alarmId, out alarm);
        }
        if (alarm is not null)
        {
            RaiseRingingStateChanged();
        }
        return alarm;
    }

    public bool HasActiveAlarms
    {
        get
        {
            lock (_syncLock)
            {
                return !_ringingAlarms.IsEmpty;
            }
        }
    }

    public IReadOnlyList<ActiveRingingAlarm> GetTimedOutAlarms(DateTimeOffset now, TimeSpan? maxDuration = null)
    {
        var limit = maxDuration ?? MaxRingingDuration;
        lock (_syncLock)
        {
            return _ringingAlarms.Values
                .Where(a => now - a.TriggerTime >= limit)
                .ToList();
        }
    }

    public IReadOnlyList<string> DismissTimedOutAlarms(DateTimeOffset now, TimeSpan? maxDuration = null)
    {
        var limit = maxDuration ?? MaxRingingDuration;
        List<string> timedOutIds;
        lock (_syncLock)
        {
            timedOutIds = _ringingAlarms.Values
                .Where(a => now - a.TriggerTime >= limit)
                .Select(a => a.AlarmId)
                .ToList();

            foreach (var id in timedOutIds)
            {
                _ringingAlarms.TryRemove(id, out _);
            }
        }

        if (timedOutIds.Count > 0)
        {
            RaiseRingingStateChanged();
        }

        return timedOutIds;
    }

    public TimeSpan? GetDelayUntilNextExpiry(DateTimeOffset now, TimeSpan? maxDuration = null)
    {
        var limit = maxDuration ?? MaxRingingDuration;
        lock (_syncLock)
        {
            if (_ringingAlarms.IsEmpty) return null;

            var earliestExpiry = _ringingAlarms.Values
                .Select(a => a.TriggerTime + limit)
                .Min();

            var delay = earliestExpiry - now;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
    }
}
