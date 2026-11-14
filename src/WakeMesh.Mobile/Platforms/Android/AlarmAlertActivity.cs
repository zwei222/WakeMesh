using System;
using System.Linq;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

[Activity(
    Label = "アラーム",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen",
    ShowWhenLocked = true,
    TurnScreenOn = true,
    ExcludeFromRecents = true,
    LaunchMode = global::Android.Content.PM.LaunchMode.SingleInstance)]
public sealed class AlarmAlertActivity : Activity
{
    private LinearLayout? _alarmsContainer;
    private TextView? _timeView;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Turn screen on and show over lock screen
        if (Build.VERSION.SdkInt >= BuildVersionCodes.OMr1)
        {
            SetShowWhenLocked(true);
            SetTurnScreenOn(true);
            var keyguardManager = (KeyguardManager?)GetSystemService(KeyguardService);
            keyguardManager?.RequestDismissKeyguard(this, null);
        }
        else
        {
#pragma warning disable CS0618
            Window?.AddFlags(
                WindowManagerFlags.ShowWhenLocked |
                WindowManagerFlags.TurnScreenOn |
                WindowManagerFlags.KeepScreenOn |
                WindowManagerFlags.DismissKeyguard);
#pragma warning restore CS0618
        }

        BuildUi();
    }

    // Item 3: Handle OnNewIntent when another alarm triggers simultaneously
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        RefreshRingingAlarmsList();
    }

    private void BuildUi()
    {
        var rootLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent)
        };
        rootLayout.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0F1012"));
        rootLayout.SetGravity(GravityFlags.CenterHorizontal);
        var pagePadding = Dp(16);
        rootLayout.SetPadding(pagePadding, Dp(20), pagePadding, Dp(20));

        // Header Title
        var titleView = new TextView(this)
        {
            Text = "アラーム",
            TextSize = 16,
            Gravity = GravityFlags.Center
        };
        titleView.SetTextColor(global::Android.Graphics.Color.ParseColor("#B7B3AA"));
        rootLayout.AddView(titleView);

        // Large Clock Display
        _timeView = new TextView(this)
        {
            Text = DateTime.Now.ToString("HH:mm"),
            TextSize = 64,
            Gravity = GravityFlags.Center
        };
        _timeView.SetTextColor(global::Android.Graphics.Color.ParseColor("#F1EEE7"));
        _timeView.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
        _timeView.SetPadding(0, Dp(24), 0, Dp(16));
        rootLayout.AddView(_timeView);

        // DND Disclaimer Notice
        var dndNotice = new TextView(this)
        {
            Text = "マナーモードやおやすみ時間など、端末の設定で音量が制限される場合があります",
            TextSize = 13,
            Gravity = GravityFlags.Center
        };
        dndNotice.SetTextColor(global::Android.Graphics.Color.ParseColor("#8F8B83"));
        dndNotice.SetPadding(0, 0, 0, Dp(24));
        rootLayout.AddView(dndNotice);

        // Dynamic Alarms Container
        _alarmsContainer = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
        };
        rootLayout.AddView(_alarmsContainer);

        // Global Action Buttons (Bottom)
        var bottomLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
        };
        bottomLayout.SetPadding(0, Dp(24), 0, 0);

        // 一括停止ボタン (Dismiss All)
        var dismissAllButton = new global::Android.Widget.Button(this)
        {
            Text = "長押しですべて停止",
            TextSize = 16
        };
        dismissAllButton.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#212327"));
        dismissAllButton.SetMinHeight(Dp(64));
        dismissAllButton.SetTextColor(global::Android.Graphics.Color.ParseColor("#F2766B"));
        dismissAllButton.SetPadding(0, Dp(12), 0, Dp(12));
        var allStopBackground = new global::Android.Graphics.Drawables.GradientDrawable();
        allStopBackground.SetColor(global::Android.Graphics.Color.ParseColor("#212327"));
        allStopBackground.SetStroke(Dp(2), global::Android.Graphics.Color.ParseColor("#F2766B"));
        dismissAllButton.Background = allStopBackground;
        dismissAllButton.Click += (s, e) =>
        {
            Toast.MakeText(this, "誤停止防止のため、長押しでアラームを停止します", ToastLength.Short)?.Show();
        };
        dismissAllButton.LongClick += (s, e) =>
        {
            var serviceIntent = new Intent(this, typeof(WakeMeshAlarmService));
            serviceIntent.SetAction(WakeMeshAlarmService.ActionDismissAll);
            StartService(serviceIntent);
            Toast.MakeText(this, "すべてのアラームを停止しました", ToastLength.Short)?.Show();
            Finish();
        };
        bottomLayout.AddView(dismissAllButton);

        if (WakeMeshAlarmService.RingManager.GetRingingAlarms().Count >= 2)
            rootLayout.AddView(bottomLayout);

        SetContentView(rootLayout);
        RefreshRingingAlarmsList();
    }

    private void RefreshRingingAlarmsList()
    {
        if (_alarmsContainer is null) return;
        _alarmsContainer.RemoveAllViews();

        var ringingList = WakeMeshAlarmService.RingManager.GetRingingAlarms();
        if (ringingList.Count == 0)
        {
            // If manager is empty, check intent extras
            var fallbackId = Intent?.GetStringExtra("alarm_id") ?? "default";
            var fallbackName = Intent?.GetStringExtra("alarm_name") ?? "目覚ましアラーム";
            var fallbackSnooze = Intent?.GetIntExtra("snooze_minutes", 5) ?? 5;
            AddAlarmCard(new ActiveRingingAlarm(fallbackId, fallbackName, DateTimeOffset.UtcNow, fallbackSnooze));
            return;
        }

        foreach (var alarm in ringingList)
        {
            AddAlarmCard(alarm);
        }
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    private void AddAlarmCard(ActiveRingingAlarm alarm)
    {
        var card = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
        };
        card.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#17181B"));
        card.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));

        var nameView = new TextView(this)
        {
            Text = alarm.AlarmName,
            TextSize = 22,
            Gravity = GravityFlags.Center
        };
        nameView.SetTextColor(global::Android.Graphics.Color.ParseColor("#F1EEE7"));
        nameView.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
        card.AddView(nameView);

        // Buttons row
        var buttonsRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
        };
        buttonsRow.SetPadding(0, Dp(16), 0, 0);

        // Snooze button
        var snoozeBtn = new global::Android.Widget.Button(this)
        {
            Text = $"スヌーズ ({alarm.SnoozeMinutes}分)",
            TextSize = 15,
            LayoutParameters = new LinearLayout.LayoutParams(0, Dp(64), 1.0f)
        };
        snoozeBtn.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#212327"));
        snoozeBtn.SetTextColor(global::Android.Graphics.Color.ParseColor("#F1EEE7"));
        snoozeBtn.Click += (s, e) =>
        {
            var serviceIntent = new Intent(this, typeof(WakeMeshAlarmService));
            serviceIntent.SetAction(WakeMeshAlarmService.ActionSnooze);
            serviceIntent.PutExtra("alarm_id", alarm.AlarmId);
            serviceIntent.PutExtra("alarm_name", alarm.AlarmName);
            serviceIntent.PutExtra("snooze_minutes", alarm.SnoozeMinutes);
            StartService(serviceIntent);

            if (!WakeMeshAlarmService.RingManager.HasActiveAlarms)
            {
                Finish();
            }
            else
            {
                RefreshRingingAlarmsList();
            }
        };
        buttonsRow.AddView(snoozeBtn);

        // Spacer
        var spacer = new global::Android.Views.View(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(Dp(12), ViewGroup.LayoutParams.MatchParent)
        };
        buttonsRow.AddView(spacer);

        // Individual Stop button
        var stopBtn = new global::Android.Widget.Button(this)
        {
            Text = "停止",
            TextSize = 20,
            LayoutParameters = new LinearLayout.LayoutParams(0, Dp(64), 1.0f)
        };
        stopBtn.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#F1EEE7"));
        stopBtn.SetTextColor(global::Android.Graphics.Color.ParseColor("#0F1012"));
        stopBtn.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
        stopBtn.Click += (s, e) =>
        {
            var serviceIntent = new Intent(this, typeof(WakeMeshAlarmService));
            serviceIntent.SetAction(WakeMeshAlarmService.ActionDismiss);
            serviceIntent.PutExtra("alarm_id", alarm.AlarmId);
            StartService(serviceIntent);

            if (!WakeMeshAlarmService.RingManager.HasActiveAlarms)
            {
                Finish();
            }
            else
            {
                RefreshRingingAlarmsList();
            }
        };
        buttonsRow.AddView(stopBtn);

        card.AddView(buttonsRow);

        var marginParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent);
        marginParams.SetMargins(0, Dp(8), 0, Dp(8));
        card.LayoutParameters = marginParams;

        _alarmsContainer?.AddView(card);
    }
}
