using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class WakeMeshAlarmReceiver : BroadcastReceiver
{
    public const string ActionAlarmTrigger = AlarmIntentHelper.ActionAlarmTrigger;
    public const string ActionSnoozeTrigger = AlarmIntentHelper.ActionSnoozeTrigger;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;

        // GoAsync provides a finite grace period (~10-15s) from Android OS to launch
        // the Foreground Service and perform bounded rescheduling. It does NOT grant
        // unlimited background execution or process survival.
        var pendingResult = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var action = intent.Action ?? ActionAlarmTrigger;
                var alarmId = intent.GetStringExtra("alarm_id");

                LocalAlarmModel? alarm = null;
                bool storeReadSucceeded = false;
                var store = new JsonFileAlarmStore(AlarmStoragePathHelper.GetAlarmsJsonPath());

                if (!string.IsNullOrWhiteSpace(alarmId))
                {
                    try
                    {
                        alarm = await store.GetByIdAsync(alarmId);
                        storeReadSucceeded = true;
                    }
                    catch (Exception)
                    {
                        // ストア破損または読み込み失敗時は起床優先 (鳴動を抑制しない)
                        storeReadSucceeded = false;
                    }
                }
                else
                {
                    // alarmIdが空の場合も念のためストア読み込み成功として扱わず起床を優先
                    storeReadSucceeded = false;
                }

                var decision = AlarmFiringDecisionHelper.Decide(
                    action,
                    alarm,
                    storeReadSucceeded,
                    DateTimeOffset.UtcNow);

                // 削除または無効を確認できたときのみ抑止
                if (decision == FiringDecision.Suppress)
                {
                    return;
                }

                // 起床優先: Foreground Serviceを速やかに起動
                StartAlarmForegroundService(context, intent, alarm);

                // 後続の再計算・保存処理 (万一保存や登録に失敗しても鳴動は既に起動済みであり殺さない)
                if (!string.IsNullOrWhiteSpace(alarmId) && storeReadSucceeded && alarm is not null)
                {
                    try
                    {
                        var holiday = new JapaneseHolidayService(cacheFilePath: AlarmStoragePathHelper.GetHolidayCacheFilePath());
                        var calc = new AlarmScheduleCalculator(holiday);
                        var scheduler = new AndroidAlarmScheduler(context);

                        await AlarmFiringDecisionHelper.HandlePostFireRescheduleAsync(
                            store,
                            calc,
                            scheduler,
                            alarmId,
                            action,
                            DateTimeOffset.UtcNow);
                    }
                    catch (Exception)
                    {
                        // 後続保存・再スケジュール失敗でも鳴動は継続
                    }
                }
            }
            catch (Exception)
            {
                // Receiverスコープ内での未処理例外を防止
            }
            finally
            {
                pendingResult?.Finish();
            }
        });
    }

    private static void StartAlarmForegroundService(Context context, Intent intent, LocalAlarmModel? alarm = null)
    {
        var serviceIntent = new Intent(context, typeof(WakeMeshAlarmService));
        serviceIntent.SetAction(WakeMeshAlarmService.ActionStartAlarm);
        serviceIntent.PutExtras(intent);

        if (!serviceIntent.HasExtra("sound_uri") && !string.IsNullOrWhiteSpace(alarm?.SoundUri))
        {
            serviceIntent.PutExtra("sound_uri", alarm.SoundUri);
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(serviceIntent);
        }
        else
        {
            context.StartService(serviceIntent);
        }
    }
}
