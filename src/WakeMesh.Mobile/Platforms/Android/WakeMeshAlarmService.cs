using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
public sealed class WakeMeshAlarmService : Service
{
    public const string ChannelId = "wakemesh_alarm_channel";
    public const int NotificationId = 1001;

    public const string ActionStartAlarm = "wakemesh.mobile.START_ALARM";
    public const string ActionDismiss = "wakemesh.mobile.DISMISS";
    public const string ActionDismissAll = "wakemesh.mobile.DISMISS_ALL";
    public const string ActionSnooze = "wakemesh.mobile.SNOOZE";

    public static ActiveAlarmRingManager RingManager => ActiveAlarmRingManager.Shared;

    private MediaPlayer? _mediaPlayer;
    private Ringtone? _fallbackRingtone;
    private Vibrator? _vibrator;
    private PowerManager.WakeLock? _wakeLock;
    private CancellationTokenSource? _timeoutCts;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent is null) return StartCommandResult.NotSticky;

        var action = intent.Action;
        var alarmId = intent.GetStringExtra("alarm_id") ?? "unknown";
        var alarmName = intent.GetStringExtra("alarm_name") ?? "アラーム";
        var snoozeMinutes = intent.GetIntExtra("snooze_minutes", 5);
        var volumePercent = intent.GetIntExtra("volume_percent", 100);
        var enableVib = intent.GetBooleanExtra("enable_vibration", true);
        var soundUri = intent.GetStringExtra("sound_uri");

        if (action == ActionDismissAll)
        {
            RingManager.DismissAll();
            StopAllRinging();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (action == ActionDismiss)
        {
            var hasRemaining = RingManager.DismissAlarm(alarmId);
            if (!hasRemaining)
            {
                StopAllRinging();
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            UpdateForegroundNotification();
            return StartCommandResult.Sticky;
        }

        if (action == ActionSnooze)
        {
            // Requirement 5: Service snooze Task.Run直後StopSelf禁止。
            // 保存とOS登録をawait完了してから停止、失敗時は操作可能な通知/状態を維持、無音成功扱い禁止。
            StopAudioAndVibrationOnly();
            _ = HandleSnoozeAsync(alarmId, alarmName, snoozeMinutes, startId);
            return StartCommandResult.Sticky;
        }

        // ActionStartAlarm: Add to RingManager
        var ringingItem = new ActiveRingingAlarm(
            alarmId,
            alarmName,
            DateTimeOffset.UtcNow,
            snoozeMinutes,
            volumePercent,
            enableVib,
            soundUri);

        var isFirstAlarm = RingManager.AddRingingAlarm(ringingItem);

        AcquireWakeLock();

        if (isFirstAlarm)
        {
            StartAudioAndVibration(ringingItem);
        }

        StartTimeoutWatcher();
        UpdateForegroundNotification();
        LaunchOrUpdateAlertActivity(ringingItem);

        return StartCommandResult.Sticky;
    }

    private async Task HandleSnoozeAsync(string alarmId, string alarmName, int snoozeMinutes, int startId)
    {
        var store = new JsonFileAlarmStore(AlarmStoragePathHelper.GetAlarmsJsonPath());
        var scheduler = new AndroidAlarmScheduler(this);

        var result = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(
            store,
            scheduler,
            alarmId,
            snoozeMinutes,
            DateTimeOffset.UtcNow);

        if (result.Success)
        {
            // 保存とOS登録完了を確認してから停止
            RingManager.SnoozeAlarm(alarmId);

            if (!RingManager.HasActiveAlarms)
            {
                StopAllRinging();
                StopSelf(startId);
                return;
            }

            // 残りのアラームが存在する場合は鳴動再開
            var remaining = RingManager.GetRingingAlarms();
            if (remaining.Count > 0)
            {
                StartAudioAndVibration(remaining[0]);
                UpdateForegroundNotification();
            }
        }
        else
        {
            // 失敗時は元鳴動再開と操作可能通知保持、成功時だけsnooze完了
            var currentAlarms = RingManager.GetRingingAlarms();
            var targetAlarm = currentAlarms.FirstOrDefault(a => a.AlarmId == alarmId) ?? currentAlarms.FirstOrDefault();
            if (targetAlarm is not null)
            {
                StartAudioAndVibration(targetAlarm);
            }
            ShowSnoozeFailureNotification(alarmId, alarmName, snoozeMinutes, result.ErrorMessage ?? "スヌーズ登録失敗");
        }
    }

    private void ShowSnoozeFailureNotification(string alarmId, string alarmName, int snoozeMinutes, string error)
    {
        var dismissUri = AlarmIntentHelper.CreateDismissUri(alarmId);
        var dismissIntent = new Intent(this, typeof(WakeMeshAlarmService));
        dismissIntent.SetAction(ActionDismiss);
        dismissIntent.SetData(global::Android.Net.Uri.Parse(dismissUri));
        dismissIntent.PutExtra("alarm_id", alarmId);
        var pendingDismiss = PendingIntent.GetService(
            this,
            AlarmIntentHelper.GetDeterministicRequestCode(dismissUri),
            dismissIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var snoozeUri = AlarmIntentHelper.CreateSnoozeUri(alarmId);
        var snoozeIntent = new Intent(this, typeof(WakeMeshAlarmService));
        snoozeIntent.SetAction(ActionSnooze);
        snoozeIntent.SetData(global::Android.Net.Uri.Parse(snoozeUri));
        snoozeIntent.PutExtra("alarm_id", alarmId);
        snoozeIntent.PutExtra("alarm_name", alarmName);
        snoozeIntent.PutExtra("snooze_minutes", snoozeMinutes);
        var pendingSnooze = PendingIntent.GetService(
            this,
            AlarmIntentHelper.GetDeterministicRequestCode(snoozeUri),
            snoozeIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(global::Android.Resource.Drawable.StatNotifyError);
        builder.SetContentTitle("WakeMesh スヌーズ登録失敗");
        builder.SetContentText($"{alarmName}: {error}。再試行または停止してください。");
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetCategory(NotificationCompat.CategoryError);
        builder.SetOngoing(true);
        builder.SetAutoCancel(false);
        builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "停止", pendingDismiss);
        builder.AddAction(global::Android.Resource.Drawable.IcMediaPlay, "再試行", pendingSnooze);

        var notification = builder.Build();
        if (notification is not null)
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void StartAudioAndVibration(ActiveRingingAlarm alarm)
    {
        // 1. Audio playback (MediaPlayer with fallback Ringtone)
        try
        {
            StartAudioPlayback(alarm);
        }
        catch (Exception)
        {
            PlayFallbackRingtone();
        }

        // 2. Vibration (always attempted independently even if audio playback fails)
        try
        {
            if (alarm.EnableVibration)
            {
                StartVibration();
            }
        }
        catch (Exception) { }
    }

    private void StartAudioPlayback(ActiveRingingAlarm alarm)
    {
        var audioBuilder = new AudioAttributes.Builder();
        audioBuilder.SetUsage(AudioUsageKind.Alarm);
        audioBuilder.SetContentType(AudioContentType.Sonification);
        var audioAttributes = audioBuilder.Build();

        _mediaPlayer?.Release();
        _mediaPlayer = new MediaPlayer();
        if (audioAttributes is not null)
        {
            _mediaPlayer.SetAudioAttributes(audioAttributes);
        }
        _mediaPlayer.Looping = true;

        bool dataSourceSet = false;
        var ctx = ApplicationContext ?? this;

        if (SoundOption.FromUri(alarm.SoundUri).Id == SoundOption.BuiltinChimeId)
        {
            try
            {
                var afd = ctx.Assets?.OpenFd("wakemesh_chime.wav");
                if (afd != null)
                {
                    _mediaPlayer.SetDataSource(afd.FileDescriptor, afd.StartOffset, afd.Length);
                    afd.Close();
                    dataSourceSet = true;
                }
            }
            catch
            {
                // Fall back to system ringtone
            }
        }

        if (!dataSourceSet)
        {
            global::Android.Net.Uri? soundUri = null;
            if (!string.IsNullOrWhiteSpace(alarm.SoundUri) &&
                SoundOption.FromUri(alarm.SoundUri).Id != SoundOption.BuiltinChimeId &&
                SoundOption.FromUri(alarm.SoundUri).Id != SoundOption.DefaultOsId)
            {
                try { soundUri = global::Android.Net.Uri.Parse(alarm.SoundUri); } catch { }
            }
            soundUri ??= RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
                         ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);

            if (soundUri is not null)
            {
                _mediaPlayer.SetDataSource(ctx, soundUri);
                dataSourceSet = true;
            }
        }

        if (dataSourceSet)
        {
            _mediaPlayer.Prepare();
            float vol = Math.Clamp(alarm.VolumePercent / 100f, 0.05f, 1.0f);
            _mediaPlayer.SetVolume(vol, vol);
            _mediaPlayer.Start();
        }
        else
        {
            PlayFallbackRingtone();
        }
    }

    private void PlayFallbackRingtone()
    {
        try
        {
            if (_fallbackRingtone?.IsPlaying == true)
            {
                _fallbackRingtone.Stop();
            }
            _fallbackRingtone?.Dispose();
            _fallbackRingtone = null;

            var uri = RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
                      ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);
            var ctx = ApplicationContext ?? this;
            _fallbackRingtone = RingtoneManager.GetRingtone(ctx, uri);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P && _fallbackRingtone != null)
            {
                _fallbackRingtone.Looping = true;
            }
            _fallbackRingtone?.Play();
        }
        catch (Exception) { }
    }

    private void StartVibration()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var vibratorManager = (VibratorManager?)GetSystemService(VibratorManagerService);
            _vibrator = vibratorManager?.DefaultVibrator;
        }
        else
        {
            _vibrator = (Vibrator?)GetSystemService(VibratorService);
        }

        if (_vibrator is not null && _vibrator.HasVibrator)
        {
            long[] pattern = { 0, 800, 400, 800, 400 };
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                _vibrator.Vibrate(VibrationEffect.CreateWaveform(pattern, 0));
            }
            else
            {
                _vibrator.Vibrate(pattern, 0);
            }
        }
    }

    // Requirement 6: タイムアウトは各鳴動の開始から10分、後発件を早期停止しない
    private void StartTimeoutWatcher()
    {
        _timeoutCts?.Cancel();
        _timeoutCts = new CancellationTokenSource();
        var token = _timeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && RingManager.HasActiveAlarms)
                {
                    var delay = RingManager.GetDelayUntilNextExpiry(DateTimeOffset.UtcNow);
                    if (delay is null) break;

                    var waitTime = delay.Value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay.Value;
                    await Task.Delay(waitTime, token);

                    if (token.IsCancellationRequested) break;

                    var timedOutIds = RingManager.DismissTimedOutAlarms(DateTimeOffset.UtcNow);
                    if (timedOutIds.Count > 0)
                    {
                        if (!RingManager.HasActiveAlarms)
                        {
                            StopAllRinging();
                            StopSelf();
                            break;
                        }

                        UpdateForegroundNotification();
                    }
                }
            }
            catch (System.OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                if (!RingManager.HasActiveAlarms)
                {
                    StopAllRinging();
                    StopSelf();
                }
            }
        }, token);
    }

    private void UpdateForegroundNotification()
    {
        var ringing = RingManager.GetRingingAlarms();
        if (ringing.Count == 0) return;

        var first = ringing[0];
        var title = ringing.Count == 1
            ? "アラームが鳴っています"
            : $"アラームが鳴っています ({ringing.Count}件同時)";
        var content = ringing.Count == 1
            ? $"{first.AlarmName} が鳴っています"
            : string.Join(", ", ringing.Select(r => r.AlarmName));

        var dismissUri = AlarmIntentHelper.CreateDismissUri(first.AlarmId);
        var dismissIntent = new Intent(this, typeof(WakeMeshAlarmService));
        dismissIntent.SetAction(ringing.Count > 1 ? ActionDismissAll : ActionDismiss);
        dismissIntent.SetData(global::Android.Net.Uri.Parse(dismissUri));
        dismissIntent.PutExtra("alarm_id", first.AlarmId);
        var pendingDismiss = PendingIntent.GetService(
            this,
            AlarmIntentHelper.GetDeterministicRequestCode(dismissUri),
            dismissIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var snoozeUri = AlarmIntentHelper.CreateSnoozeUri(first.AlarmId);
        var snoozeIntent = new Intent(this, typeof(WakeMeshAlarmService));
        snoozeIntent.SetAction(ActionSnooze);
        snoozeIntent.SetData(global::Android.Net.Uri.Parse(snoozeUri));
        snoozeIntent.PutExtra("alarm_id", first.AlarmId);
        snoozeIntent.PutExtra("alarm_name", first.AlarmName);
        snoozeIntent.PutExtra("snooze_minutes", first.SnoozeMinutes);
        var pendingSnooze = PendingIntent.GetService(
            this,
            AlarmIntentHelper.GetDeterministicRequestCode(snoozeUri),
            snoozeIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var alertUri = AlarmIntentHelper.CreateAlertUri(first.AlarmId);
        var fullScreenIntent = new Intent(this, typeof(AlarmAlertActivity));
        fullScreenIntent.SetData(global::Android.Net.Uri.Parse(alertUri));
        fullScreenIntent.PutExtra("alarm_id", first.AlarmId);
        fullScreenIntent.PutExtra("alarm_name", first.AlarmName);
        fullScreenIntent.PutExtra("snooze_minutes", first.SnoozeMinutes);
        fullScreenIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        var pendingFullScreen = PendingIntent.GetActivity(
            this,
            AlarmIntentHelper.GetDeterministicRequestCode(alertUri),
            fullScreenIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var dismissLabel = ringing.Count > 1 ? "すべて停止" : "停止";
        var notificationBuilder = new NotificationCompat.Builder(this, ChannelId);
        notificationBuilder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm);
        notificationBuilder.SetContentTitle(title);
        notificationBuilder.SetContentText(content);
        notificationBuilder.SetPriority(NotificationCompat.PriorityMax);
        notificationBuilder.SetCategory(NotificationCompat.CategoryAlarm);
        notificationBuilder.SetVisibility(NotificationCompat.VisibilityPublic);
        notificationBuilder.SetOngoing(true);
        notificationBuilder.SetAutoCancel(false);
        notificationBuilder.SetFullScreenIntent(pendingFullScreen, true);
        notificationBuilder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, dismissLabel, pendingDismiss);
        notificationBuilder.AddAction(global::Android.Resource.Drawable.IcMediaPlay, $"スヌーズ ({first.SnoozeMinutes}分)", pendingSnooze);

        var notification = notificationBuilder.Build();
        if (notification is not null)
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void LaunchOrUpdateAlertActivity(ActiveRingingAlarm alarm)
    {
        var alertUri = AlarmIntentHelper.CreateAlertUri(alarm.AlarmId);
        var intent = new Intent(this, typeof(AlarmAlertActivity));
        intent.SetData(global::Android.Net.Uri.Parse(alertUri));
        intent.PutExtra("alarm_id", alarm.AlarmId);
        intent.PutExtra("alarm_name", alarm.AlarmName);
        intent.PutExtra("snooze_minutes", alarm.SnoozeMinutes);
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        StartActivity(intent);
    }

    private void AcquireWakeLock()
    {
        var powerManager = (PowerManager?)GetSystemService(PowerService);
        if (powerManager is not null)
        {
            _wakeLock ??= powerManager.NewWakeLock(WakeLockFlags.Partial, "WakeMesh::AlarmWakeLock");
            if (_wakeLock?.IsHeld == false)
            {
                _wakeLock.Acquire(10 * 60 * 1000L); // 10分上限
            }
        }
    }

    private void StopAudioAndVibrationOnly()
    {
        try
        {
            if (_mediaPlayer?.IsPlaying == true)
            {
                _mediaPlayer.Stop();
            }
            _mediaPlayer?.Release();
            _mediaPlayer = null;
        }
        catch (Exception) { }

        try
        {
            if (_fallbackRingtone?.IsPlaying == true)
            {
                _fallbackRingtone.Stop();
            }
            _fallbackRingtone?.Dispose();
            _fallbackRingtone = null;
        }
        catch (Exception) { }

        try
        {
            _vibrator?.Cancel();
            _vibrator = null;
        }
        catch (Exception) { }
    }

    private void StopAllRinging()
    {
        _timeoutCts?.Cancel();
        _timeoutCts = null;
        StopAudioAndVibrationOnly();

        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
    }

    // Requirement 6: OnDestroyでstatic RingManagerを完全クリアし後続発火が無音にならない
    public override void OnDestroy()
    {
        StopAllRinging();
        RingManager.DismissAll();
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "アラームの通知",
                NotificationImportance.Max)
            {
                Description = "目覚ましアラームの通知と停止操作を行う重要通知",
                LockscreenVisibility = NotificationVisibility.Public
            };
            channel.EnableVibration(true);

            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
    }
}
