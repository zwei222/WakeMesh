using System;
using System.Linq;
using System.Collections.Generic;
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
    private global::Android.Views.View? _allStopRow;
    private sealed class CardState
    {
        public required ActiveRingingAlarm Alarm;
        public required LinearLayout Card;
        public required TextView Status;
        public required TextView Name;
        public required SlideToConfirmView Stop;
        public required SlideToConfirmView Snooze;
        public int Generation;
        public bool Removing;
        public bool Fallback;
    }
    private readonly Dictionary<string, CardState> _cards = new();
    private readonly RingingInteractionState _state = new();
    private readonly Handler _handler = new(Looper.MainLooper!);

    private bool _observedNonEmpty;

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
                ViewGroup.LayoutParams.WrapContent)
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
            var gen = _state.BeginAllStop();
            dismissAllButton.Text = "すべて停止しています…";
            _handler.PostDelayed(() => { if (!IsFinishing && !IsDestroyed && _state.TimeoutAllStop(gen)) { dismissAllButton.Text = "長押しですべて停止"; Toast.MakeText(this, "すべて停止を確認できませんでした。もう一度長押ししてください", ToastLength.Short)?.Show(); } }, 3000);
        };
        _dismissAllButton = dismissAllButton;
        bottomLayout.AddView(dismissAllButton);
        rootLayout.AddView(bottomLayout);
        _allStopRow = bottomLayout;
        bottomLayout.Visibility = WakeMeshAlarmService.RingManager.GetRingingAlarms().Count >= 2 ? ViewStates.Visible : ViewStates.Gone;

        var scroll = new global::Android.Widget.ScrollView(this) { FillViewport = true };
        scroll.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0F1012"));
        scroll.AddView(rootLayout);
        SetContentView(scroll);
        RefreshRingingAlarmsList();
    }

    protected override void OnStart()
    {
        base.OnStart();
        WakeMeshAlarmService.RingManager.RingingStateChanged += OnRingingChanged;
        WakeMeshAlarmService.RingManager.SnoozeOutcome += OnSnoozeOutcome;
        RefreshRingingAlarmsList();
    }

    protected override void OnStop()
    {
        WakeMeshAlarmService.RingManager.RingingStateChanged -= OnRingingChanged;
        WakeMeshAlarmService.RingManager.SnoozeOutcome -= OnSnoozeOutcome;
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        _handler.RemoveCallbacksAndMessages(null);
        base.OnDestroy();
    }

    private void OnRingingChanged(object? sender, EventArgs e) => OnUi(RefreshRingingAlarmsList);
    private void OnSnoozeOutcome(object? sender, RingingActionOutcome e) => OnUi(() => HandleSnoozeOutcome(e));
    private void OnUi(Action action) => RunOnUiThread(() => { if (!IsFinishing && !IsDestroyed) action(); });

    private void RefreshRingingAlarmsList()
    {
        if (_alarmsContainer is null) return;
        var alarms = WakeMeshAlarmService.RingManager.GetRingingAlarms();
        var ids = alarms.Select(a => a.AlarmId).ToHashSet();
        if (alarms.Count == 0 && _cards.Count == 0 && !_observedNonEmpty && !string.IsNullOrEmpty(Intent?.GetStringExtra("alarm_id")))
            AddAlarmCard(new ActiveRingingAlarm(Intent.GetStringExtra("alarm_id")!, Intent.GetStringExtra("alarm_name") ?? "アラーム", DateTimeOffset.Now, Intent.GetIntExtra("snooze_minutes", 5)), fallback: true);
        foreach (var alarm in alarms)
        {
            if (!_cards.TryGetValue(alarm.AlarmId, out var ex)) { AddAlarmCard(alarm); continue; }
            if (ex.Removing) { var idx = _alarmsContainer.IndexOfChild(ex.Card); _state.Forget(alarm.AlarmId); DetachCard(ex); AddAlarmCard(alarm, idx); }
            else if (_state.PendingAction(alarm.AlarmId) != null) { }
            else if (ex.Alarm.AlarmName != alarm.AlarmName || ex.Alarm.SnoozeMinutes != alarm.SnoozeMinutes) { var idx = _alarmsContainer.IndexOfChild(ex.Card); DetachCard(ex); AddAlarmCard(alarm, idx); }
            else { ex.Alarm = alarm; ex.Fallback = false; }
        }
        if (_state.ObserveAllStop(alarms.Count)) { Toast.MakeText(this, "すべてのアラームを停止しました", ToastLength.Short)?.Show(); Finish(); return; }
        foreach (var card in _cards.Values.ToArray().Where(c => !ids.Contains(c.Alarm.AlarmId) && !c.Removing))
        {
            var id = card.Alarm.AlarmId;
            if (_state.ObserveActive(id, false)) CompleteCard(card, "停止しました");
            else if (_state.PendingAction(id) == RingingAction.Snooze) continue;
            else if (card.Fallback) continue;
            else { _state.Forget(id); DetachCard(card); }
        }
        if (_allStopRow != null) _allStopRow.Visibility = alarms.Count >= 2 ? ViewStates.Visible : ViewStates.Gone;
        FinishIfIdle();
    }

    private global::Android.Widget.Button? _dismissAllButton;

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    private void AddAlarmCard(ActiveRingingAlarm alarm, int index = -1, bool fallback = false)
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#17181B"));
        card.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        var name = new TextView(this) { Text = alarm.AlarmName, TextSize = 22, Gravity = GravityFlags.Center };
        name.SetTextColor(global::Android.Graphics.Color.ParseColor("#F1EEE7"));
        card.AddView(name);
        var status = new TextView(this) { TextSize = 13, Gravity = GravityFlags.Center, AccessibilityLiveRegion = global::Android.Views.AccessibilityLiveRegion.Polite };
        status.SetTextColor(global::Android.Graphics.Color.ParseColor("#F2766B")); card.AddView(status);
        var note = new TextView(this) { Text = "右へスライドして操作", TextSize = 13, Gravity = GravityFlags.Center };
        note.SetTextColor(global::Android.Graphics.Color.ParseColor("#8F8B83")); card.AddView(note);
        CardState? state = null;
        var snooze = new SlideToConfirmView(this, $"スヌーズ ({alarm.SnoozeMinutes}分)", "離してスヌーズ", () => { if (state != null) StartCardOperation(state, RingingAction.Snooze); }, status);
        var stop = new SlideToConfirmView(this, "停止", "離して停止", () => { if (state != null) StartCardOperation(state, RingingAction.Stop); }, status, true);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent); lp.SetMargins(0, Dp(6), 0, Dp(6));
        card.AddView(snooze, lp); card.AddView(stop, lp);
        var margin = new LinearLayout.LayoutParams(-1, -2); margin.SetMargins(0, Dp(8), 0, Dp(8)); card.LayoutParameters = margin;
        _alarmsContainer?.AddView(card, index);
        state = new CardState { Alarm = alarm, Card = card, Name = name, Status = status, Stop = stop, Snooze = snooze, Fallback = fallback };
        _cards[alarm.AlarmId] = state;
        _observedNonEmpty = true;
    }

    private void StartCardOperation(CardState card, RingingAction action, bool retry = false)
    {
        if (IsFinishing || IsDestroyed || !_state.Begin(card.Alarm.AlarmId, action, retry)) return;
        var generation = ++card.Generation; card.Stop.Enabled = card.Snooze.Enabled = false;
        var pending = action == RingingAction.Stop ? "停止しています…" : "スヌーズを設定しています…";
        (action == RingingAction.Stop ? card.Stop : card.Snooze).SetProgressFull();
        (action == RingingAction.Stop ? card.Stop : card.Snooze).SetStatusText(pending);
        card.Status.Text = pending;
        var intent = new Intent(this, typeof(WakeMeshAlarmService));
        if (action == RingingAction.Stop) { intent.SetAction(WakeMeshAlarmService.ActionDismiss); intent.PutExtra("alarm_id", card.Alarm.AlarmId); }
        else { intent.SetAction(WakeMeshAlarmService.ActionSnooze); intent.PutExtra("alarm_id", card.Alarm.AlarmId); intent.PutExtra("alarm_name", card.Alarm.AlarmName); intent.PutExtra("snooze_minutes", card.Alarm.SnoozeMinutes); }
        StartService(intent);
        var timeout = action == RingingAction.Stop ? 3000 : 15000;
        _handler.PostDelayed(() => { if (!IsFinishing && !IsDestroyed && IsCurrent(card) && card.Generation == generation) { if (action == RingingAction.Snooze && !WakeMeshAlarmService.RingManager.GetRingingAlarms().Any(a => a.AlarmId == card.Alarm.AlarmId)) { _state.Forget(card.Alarm.AlarmId); DetachCard(card); FinishIfIdle(); } else { _state.Timeout(card.Alarm.AlarmId); ResetCard(card, action == RingingAction.Stop ? "停止を確認できませんでした。もう一度スライドしてください" : "スヌーズを確認できませんでした。もう一度スライドしてください"); } } }, timeout);
    }

    private void HandleSnoozeOutcome(RingingActionOutcome outcome)
    {
        if (!_cards.TryGetValue(outcome.AlarmId, out var card) || card.Removing) return;
        var active = WakeMeshAlarmService.RingManager.GetRingingAlarms().Any(a => a.AlarmId == outcome.AlarmId);
        if (_state.OnSnoozeOutcome(outcome.AlarmId, outcome.Success, active)) CompleteCard(card, $"スヌーズ中・{card.Alarm.SnoozeMinutes}分後にもう一度鳴ります");
        else if (!outcome.Success && _state.PendingAction(outcome.AlarmId) != RingingAction.Stop && _state.OutstandingSends(outcome.AlarmId) == 0) ResetCard(card, "スヌーズできませんでした。もう一度スライドするか、停止してください。");
    }
    private void ResetCard(CardState card, string message) { card.Generation++; card.Status.Text = message; card.Stop.Enabled = card.Snooze.Enabled = true; card.Stop.ResetProgress(); card.Snooze.ResetProgress(); }
    private void CompleteCard(CardState card, string message)
    {
        if (card.Removing) return; card.Removing = true; card.Status.Text = message; card.Stop.SetStatusText(message); card.Snooze.SetStatusText(message);
        card.Stop.Enabled = card.Snooze.Enabled = false;
        var generation = ++card.Generation;
        _handler.PostDelayed(() => { if (IsDestroyed || IsFinishing) return; _alarmsContainer?.RemoveView(card.Card); if (IsCurrent(card)) _cards.Remove(card.Alarm.AlarmId); FinishIfIdle(); }, 800);
    }
    private bool IsCurrent(CardState c) => _cards.TryGetValue(c.Alarm.AlarmId, out var cur) && ReferenceEquals(cur, c);
    private void DetachCard(CardState c) { c.Generation++; c.Removing = true; _alarmsContainer?.RemoveView(c.Card); if (IsCurrent(c)) _cards.Remove(c.Alarm.AlarmId); }
    private void FinishIfIdle() { if (!_state.AllStopPending && _cards.Count == 0 && _observedNonEmpty) Finish(); }
}
