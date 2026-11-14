using System;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public enum FiringDecision
{
    Ring,
    Suppress
}

public static class AlarmFiringDecisionHelper
{
    public static FiringDecision Decide(
        string action,
        LocalAlarmModel? alarm,
        bool storeReadSucceeded,
        DateTimeOffset now)
    {
        // 1. ストア読み込み失敗/破損時は起床優先で鳴動
        if (!storeReadSucceeded)
        {
            return FiringDecision.Ring;
        }

        // 2. 削除を確認できたときのみ抑止
        if (alarm is null)
        {
            return FiringDecision.Suppress;
        }

        // 3. スヌーズ発火の場合: IsEnabledが無効化された単発でも起床鳴動優先
        if (action == AlarmIntentHelper.ActionSnoozeTrigger)
        {
            return FiringDecision.Ring;
        }

        // 4. 通常アラーム発火の場合:
        // 未来SnoozeUntilが存在する場合はスヌーズ対象として鳴動
        if (alarm.SnoozeUntil.HasValue && alarm.SnoozeUntil.Value > now.AddMinutes(-5))
        {
            return FiringDecision.Ring;
        }

        // 無効を確認できたときのみ抑止
        if (!alarm.IsEnabled)
        {
            return FiringDecision.Suppress;
        }

        return FiringDecision.Ring;
    }

    public static async Task HandlePostFireRescheduleAsync(
        IAlarmStore store,
        AlarmScheduleCalculator calculator,
        IAlarmScheduler scheduler,
        string alarmId,
        string action,
        DateTimeOffset now)
    {
        try
        {
            await store.UpdateAsync(alarmId, alarm =>
            {
                if (action == AlarmIntentHelper.ActionSnoozeTrigger)
                {
                    // Snooze has fired; clear persisted snooze target
                    alarm.SnoozeUntil = null;
                }

                if (alarm.RepeatKind == AlarmRepeatKind.Weekly && alarm.IsEnabled)
                {
                    var nextFire = calculator.CalculateNextFire(alarm, now);
                    alarm.NextFireTime = nextFire?.Instant;
                }
                else if (alarm.RepeatKind == AlarmRepeatKind.Once)
                {
                    // One-off alarm finished (both normal fire and snooze fire complete the once alarm)
                    alarm.IsEnabled = false;
                    alarm.NextFireTime = null;
                    alarm.OsRegistrationStatus = AlarmRegistrationStatus.Unregistered;
                }
            });

            var updated = await store.GetByIdAsync(alarmId);
            if (updated is not null && updated.RepeatKind == AlarmRepeatKind.Weekly && updated.IsEnabled && updated.NextFireTime.HasValue)
            {
                try
                {
                    await scheduler.ScheduleAlarmAsync(updated, updated.NextFireTime.Value);
                    await store.UpdateAsync(alarmId, a =>
                    {
                        a.OsRegistrationStatus = AlarmRegistrationStatus.Scheduled;
                        a.OsRegistrationError = null;
                    });
                }
                catch (Exception ex)
                {
                    await store.UpdateAsync(alarmId, a =>
                    {
                        a.OsRegistrationStatus = AlarmRegistrationStatus.Failed;
                        a.OsRegistrationError = ex.Message;
                    });
                }
            }
        }
        catch (Exception)
        {
            // 後続保存失敗でも鳴動を殺さない (Subsequent save failure must not kill ringing)
        }
    }

    public static async Task<SnoozeExecutionResult> ExecuteSnoozeAsync(
        IAlarmStore store,
        IAlarmScheduler scheduler,
        string alarmId,
        int snoozeMinutes,
        DateTimeOffset now)
    {
        try
        {
            var snoozeUntil = now.AddMinutes(snoozeMinutes);

            await store.UpdateAsync(alarmId, alarm =>
            {
                alarm.SnoozeUntil = snoozeUntil;
                alarm.NextFireTime = snoozeUntil;
            });

            var alarm = await store.GetByIdAsync(alarmId);
            if (alarm is null)
            {
                return new SnoozeExecutionResult(false, "アラームデータが見つかりませんでした。");
            }

            await scheduler.ScheduleAlarmAsync(alarm, snoozeUntil);

            await store.UpdateAsync(alarmId, a =>
            {
                a.OsRegistrationStatus = AlarmRegistrationStatus.Scheduled;
                a.OsRegistrationError = null;
            });

            return new SnoozeExecutionResult(true, null);
        }
        catch (Exception ex)
        {
            try
            {
                await store.UpdateAsync(alarmId, a =>
                {
                    a.OsRegistrationStatus = AlarmRegistrationStatus.Failed;
                    a.OsRegistrationError = ex.Message;
                });
            }
            catch { }

            return new SnoozeExecutionResult(false, ex.Message);
        }
    }
}

public sealed record SnoozeExecutionResult(bool Success, string? ErrorMessage);
