using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Sync.Tests.Fakes;

/// <summary>
/// Fake implementation of <see cref="IAlarmScheduler"/> for tests.
/// Explicitly NOT real Android AlarmManager execution.
/// </summary>
public sealed class FakeAlarmScheduler : IAlarmScheduler
{
    public sealed record ScheduledEntry(LocalAlarmModel Alarm, DateTimeOffset FireTime);

    public ConcurrentDictionary<string, ScheduledEntry> ScheduledAlarms { get; } = new(StringComparer.Ordinal);
    public List<string> CancelLog { get; } = new();

    /// <summary>Number of <see cref="ScheduleAlarmAsync"/> invocations (including failures).</summary>
    public int ScheduleCalls { get; private set; }

    /// <summary>Number of <see cref="CancelAlarmAsync"/> invocations (including failures).</summary>
    public int CancelCalls { get; private set; }

    public HashSet<string> FailOnScheduleIds { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailOnCancelIds { get; } = new(StringComparer.Ordinal);

    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ScheduleCalls++;

        if (FailOnScheduleIds.Contains(alarm.Id))
        {
            throw new InvalidOperationException($"Simulated OS failure scheduling alarm {alarm.Id}");
        }

        ScheduledAlarms[alarm.Id] = new ScheduledEntry(alarm, fireTime);
        return Task.CompletedTask;
    }

    public Task CancelAlarmAsync(string alarmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmId);
        CancelCalls++;

        if (FailOnCancelIds.Contains(alarmId))
        {
            throw new InvalidOperationException($"Simulated OS failure canceling alarm {alarmId}");
        }

        ScheduledAlarms.TryRemove(alarmId, out _);
        lock (CancelLog)
        {
            CancelLog.Add(alarmId);
        }
        return Task.CompletedTask;
    }
}
