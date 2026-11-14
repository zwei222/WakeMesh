using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] {
    Intent.ActionBootCompleted,
    "android.intent.action.QUICKBOOT_POWERON",
    "com.htc.intent.action.QUICKBOOT_POWERON",
    "android.intent.action.TIME_SET",
    "android.intent.action.TIMEZONE_CHANGED",
    "android.intent.action.MY_PACKAGE_REPLACED"
})]
public sealed class WakeMeshBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;

        var action = intent.Action;
        if (action == Intent.ActionBootCompleted ||
            action == "android.intent.action.QUICKBOOT_POWERON" ||
            action == "com.htc.intent.action.QUICKBOOT_POWERON" ||
            action == "android.intent.action.TIME_SET" ||
            action == "android.intent.action.TIMEZONE_CHANGED" ||
            action == "android.intent.action.MY_PACKAGE_REPLACED")
        {
            // Item 5: Receiver goAsync with identical storage path and synchronization
            var pendingResult = GoAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    var filePath = AlarmStoragePathHelper.GetAlarmsJsonPath();
                    var store = new JsonFileAlarmStore(filePath);
                    var holiday = new JapaneseHolidayService(cacheFilePath: AlarmStoragePathHelper.GetHolidayCacheFilePath());
                    var calc = new AlarmScheduleCalculator(holiday);
                    var scheduler = new AndroidAlarmScheduler(context);
                    var coordinator = new AlarmCoordinator(store, calc, scheduler);

                    // Reschedule active alarms while preserving active future snoozes
                    await coordinator.RescheduleAllActiveAsync();
                }
                catch (Exception)
                {
                    // Safe error containment on system event
                }
                finally
                {
                    pendingResult?.Finish();
                }
            });
        }
    }
}
