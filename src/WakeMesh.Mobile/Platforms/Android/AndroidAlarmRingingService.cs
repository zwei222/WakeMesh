#if ANDROID
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Content;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class AndroidAlarmRingingService : IAlarmRingingService
{
    public bool HasActiveAlarms => ActiveAlarmRingManager.Shared.HasActiveAlarms;

    public IReadOnlyList<ActiveRingingAlarm> GetRingingAlarms() => ActiveAlarmRingManager.Shared.GetRingingAlarms();

    public event EventHandler? RingingStateChanged
    {
        add => ActiveAlarmRingManager.Shared.RingingStateChanged += value;
        remove => ActiveAlarmRingManager.Shared.RingingStateChanged -= value;
    }

    public Task<bool> DismissAlarmAsync(string alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId)) return Task.FromResult(false);

        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(WakeMeshAlarmService));
            intent.SetAction(WakeMeshAlarmService.ActionDismiss);
            intent.PutExtra("alarm_id", alarmId);
            context.StartService(intent);
        }
        catch
        {
            // Fallback: direct dismissal in manager
            ActiveAlarmRingManager.Shared.DismissAlarm(alarmId);
        }

        return Task.FromResult(true);
    }

    public Task<bool> DismissAllAsync()
    {
        if (!HasActiveAlarms) return Task.FromResult(false);

        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(WakeMeshAlarmService));
            intent.SetAction(WakeMeshAlarmService.ActionDismissAll);
            context.StartService(intent);
        }
        catch
        {
            // Fallback: direct dismissal in manager
            ActiveAlarmRingManager.Shared.DismissAll();
        }

        return Task.FromResult(true);
    }
}
#endif
