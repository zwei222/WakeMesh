using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public sealed class AlarmCoordinator
{
    private readonly IAlarmStore _store;
    private readonly AlarmScheduleCalculator _calculator;
    private readonly IAlarmScheduler _scheduler;

    public AlarmCoordinator(
        IAlarmStore store,
        AlarmScheduleCalculator calculator,
        IAlarmScheduler scheduler)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public Task<IReadOnlyList<LocalAlarmModel>> GetAllAsync() => _store.GetAllAsync();

    public Task<LocalAlarmModel?> GetByIdAsync(string id) => _store.GetByIdAsync(id);

    public async Task<DateTimeOffset?> SaveAndScheduleAsync(LocalAlarmModel alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        DateTimeOffset? targetTime = null;
        var now = DateTimeOffset.UtcNow;

        // Evaluate future SnoozeUntil BEFORE IsEnabled (Requirement 4)
        if (alarm.SnoozeUntil.HasValue && alarm.SnoozeUntil.Value > now)
        {
            targetTime = alarm.SnoozeUntil.Value;
        }
        else
        {
            alarm.SnoozeUntil = null; // Expired snooze cleared
            if (alarm.IsEnabled)
            {
                var nextFire = _calculator.CalculateNextFire(alarm, now);
                targetTime = nextFire?.Instant;
            }
        }

        alarm.NextFireTime = targetTime;

        // 1. Atomic save to store first (Item 7: 保存後にOS登録)
        await _store.SaveAsync(alarm);

        // 2. Register to OS AlarmManager
        if (targetTime.HasValue)
        {
            try
            {
                await _scheduler.ScheduleAlarmAsync(alarm, targetTime.Value);
                alarm.OsRegistrationStatus = AlarmRegistrationStatus.Scheduled;
                alarm.OsRegistrationError = null;
            }
            catch (Exception ex)
            {
                // Item 4 & 7: OS registration failure recorded honestly
                alarm.OsRegistrationStatus = AlarmRegistrationStatus.Failed;
                alarm.OsRegistrationError = ex.Message;
            }
        }
        else
        {
            try
            {
                await _scheduler.CancelAlarmAsync(alarm.Id);
                alarm.OsRegistrationStatus = AlarmRegistrationStatus.Unregistered;
                alarm.OsRegistrationError = null;
            }
            catch (Exception ex)
            {
                alarm.OsRegistrationStatus = AlarmRegistrationStatus.Failed;
                alarm.OsRegistrationError = ex.Message;
            }
        }

        // Persist updated registration status
        await _store.SaveAsync(alarm);
        return alarm.NextFireTime;
    }

    public Task<DateTimeOffset?> SaveAlarmAsync(LocalAlarmModel alarm) => SaveAndScheduleAsync(alarm);

    public async Task<LocalAlarmModel?> ToggleAndRescheduleAsync(string alarmId)
    {
        var alarm = await _store.GetByIdAsync(alarmId);
        if (alarm is null) return null;

        alarm.IsEnabled = !alarm.IsEnabled;
        if (!alarm.IsEnabled)
        {
            alarm.SnoozeUntil = null; // Clear snooze when disabled
        }
        await SaveAndScheduleAsync(alarm);
        return alarm;
    }

    public Task<LocalAlarmModel?> ToggleAlarmEnabledAsync(string alarmId) => ToggleAndRescheduleAsync(alarmId);

    public async Task DeleteAndCancelAsync(string alarmId)
    {
        try
        {
            await _scheduler.CancelAlarmAsync(alarmId);
        }
        catch (Exception)
        {
            // Best effort cancellation on delete
        }
        await _store.DeleteAsync(alarmId);
    }

    /// <summary>
    /// Strict delete used by cloud sync: cancel the OS registration first and remove the stored alarm
    /// only after the OS acknowledged the cancellation. Any cancellation or store failure propagates to
    /// the caller (never swallowed), so callers cannot report success while an OS alarm may stay armed.
    /// The unchanged best-effort <see cref="DeleteAndCancelAsync"/> remains the UI delete contract.
    /// </summary>
    public async Task CancelThenDeleteAsync(string alarmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmId);

        // Throws on OS failure: the alarm stays in the store so a later retry can still cancel it.
        await _scheduler.CancelAlarmAsync(alarmId).ConfigureAwait(false);

        await _store.DeleteAsync(alarmId).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAlarmAsync(string alarmId)
    {
        var existing = await _store.GetByIdAsync(alarmId);
        if (existing is null) return false;
        await DeleteAndCancelAsync(alarmId);
        return true;
    }

    public async Task<int> RescheduleAllActiveAsync()
    {
        var alarms = await _store.GetAllAsync();
        int scheduledCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var alarm in alarms)
        {
            DateTimeOffset? targetTime = null;
            // Evaluate future SnoozeUntil BEFORE IsEnabled (Requirement 4)
            if (alarm.SnoozeUntil.HasValue && alarm.SnoozeUntil.Value > now)
            {
                targetTime = alarm.SnoozeUntil.Value;
            }
            else
            {
                alarm.SnoozeUntil = null;
                if (alarm.IsEnabled)
                {
                    var nextFire = _calculator.CalculateNextFire(alarm, now);
                    targetTime = nextFire?.Instant;
                }
            }

            if (targetTime.HasValue)
            {
                alarm.NextFireTime = targetTime.Value;
                await _store.SaveAsync(alarm); // Save before OS call

                try
                {
                    await _scheduler.ScheduleAlarmAsync(alarm, targetTime.Value);
                    alarm.OsRegistrationStatus = AlarmRegistrationStatus.Scheduled;
                    alarm.OsRegistrationError = null;
                    scheduledCount++;
                }
                catch (Exception ex)
                {
                    // Item 4: Honest unregistered display, continue other alarms
                    alarm.OsRegistrationStatus = AlarmRegistrationStatus.Failed;
                    alarm.OsRegistrationError = ex.Message;
                }

                await _store.SaveAsync(alarm);
                continue;
            }

            // Disabled or no next fire
            alarm.NextFireTime = null;
            alarm.OsRegistrationStatus = AlarmRegistrationStatus.Unregistered;
            await _store.SaveAsync(alarm);

            try
            {
                await _scheduler.CancelAlarmAsync(alarm.Id);
            }
            catch (Exception)
            {
                // Continue other alarms
            }
        }

        return scheduledCount;
    }
}
