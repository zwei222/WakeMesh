using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class AndroidAlarmScheduler : IAlarmScheduler
{
    private readonly Context _context;

    public AndroidAlarmScheduler(Context? context = null)
    {
        _context = context ?? global::Android.App.Application.Context;
    }

    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
    {
        var alarmManager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
        if (alarmManager is null)
        {
            throw new InvalidOperationException("Android AlarmService is unavailable.");
        }

        // Check exact alarm permission on Android 12+ (API 31+)
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S && !alarmManager.CanScheduleExactAlarms())
        {
            throw new InvalidOperationException("SCHEDULE_EXACT_ALARM permission is revoked.");
        }

        var isSnooze = alarm.SnoozeUntil.HasValue && alarm.SnoozeUntil.Value == fireTime;
        var stableUri = isSnooze
            ? AlarmIntentHelper.CreateSnoozeUri(alarm.Id)
            : AlarmIntentHelper.CreateAlarmUri(alarm.Id);

        var action = isSnooze
            ? AlarmIntentHelper.ActionSnoozeTrigger
            : AlarmIntentHelper.ActionAlarmTrigger;

        var intent = new Intent(_context, typeof(WakeMeshAlarmReceiver));
        intent.SetAction(action);
        intent.SetData(global::Android.Net.Uri.Parse(stableUri));
        intent.PutExtra("alarm_id", alarm.Id);
        intent.PutExtra("alarm_name", alarm.Name);
        intent.PutExtra("snooze_minutes", alarm.SnoozeMinutes);
        intent.PutExtra("volume_percent", alarm.VolumePercent);
        intent.PutExtra("enable_vibration", alarm.EnableVibration);
        intent.PutExtra("is_snooze", isSnooze);
        intent.PutExtra("sound_uri", alarm.SoundUri);

        int requestCode = AlarmIntentHelper.GetDeterministicRequestCode(stableUri);
        var pendingIntent = PendingIntent.GetBroadcast(
            _context,
            requestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        long triggerMillis = fireTime.ToUnixTimeMilliseconds();

        // ShowIntent for lock screen & status bar tap
        var alertUri = AlarmIntentHelper.CreateAlertUri(alarm.Id);
        var showIntent = new Intent(_context, typeof(AlarmAlertActivity));
        showIntent.SetData(global::Android.Net.Uri.Parse(alertUri));
        showIntent.PutExtra("alarm_id", alarm.Id);
        showIntent.PutExtra("alarm_name", alarm.Name);
        showIntent.PutExtra("snooze_minutes", alarm.SnoozeMinutes);
        showIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);

        int showRequestCode = AlarmIntentHelper.GetDeterministicRequestCode(alertUri);
        var pendingShowIntent = PendingIntent.GetActivity(
            _context,
            showRequestCode,
            showIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        if (pendingIntent is not null && pendingShowIntent is not null)
        {
            var alarmClockInfo = new AlarmManager.AlarmClockInfo(triggerMillis, pendingShowIntent);
            alarmManager.SetAlarmClock(alarmClockInfo, pendingIntent);
        }

        return Task.CompletedTask;
    }

    public Task CancelAlarmAsync(string alarmId)
    {
        var alarmManager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
        if (alarmManager is null)
        {
            return Task.CompletedTask;
        }

        // Cancel primary alarm
        var alarmUri = AlarmIntentHelper.CreateAlarmUri(alarmId);
        var alarmIntent = new Intent(_context, typeof(WakeMeshAlarmReceiver));
        alarmIntent.SetAction(AlarmIntentHelper.ActionAlarmTrigger);
        alarmIntent.SetData(global::Android.Net.Uri.Parse(alarmUri));
        int requestCode = AlarmIntentHelper.GetDeterministicRequestCode(alarmUri);

        var pendingAlarm = PendingIntent.GetBroadcast(
            _context,
            requestCode,
            alarmIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        if (pendingAlarm is not null)
        {
            alarmManager.Cancel(pendingAlarm);
            pendingAlarm.Cancel();
        }

        // Also cancel snooze alarm if any
        var snoozeUri = AlarmIntentHelper.CreateSnoozeUri(alarmId);
        var snoozeIntent = new Intent(_context, typeof(WakeMeshAlarmReceiver));
        snoozeIntent.SetAction(AlarmIntentHelper.ActionSnoozeTrigger);
        snoozeIntent.SetData(global::Android.Net.Uri.Parse(snoozeUri));
        int snoozeRequestCode = AlarmIntentHelper.GetDeterministicRequestCode(snoozeUri);

        var pendingSnooze = PendingIntent.GetBroadcast(
            _context,
            snoozeRequestCode,
            snoozeIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        if (pendingSnooze is not null)
        {
            alarmManager.Cancel(pendingSnooze);
            pendingSnooze.Cancel();
        }

        return Task.CompletedTask;
    }
}
