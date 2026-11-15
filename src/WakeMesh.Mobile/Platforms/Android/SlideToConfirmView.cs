using Android.Content;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using GColor = global::Android.Graphics.Color;
using GPaint = global::Android.Graphics.Paint;
using GCanvas = global::Android.Graphics.Canvas;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class SlideToConfirmView : global::Android.Views.View
{
    readonly string _label, _confirm;
    readonly System.Action _commit;
    readonly float _density;
    readonly bool _isPrimary;
    float _downX, _downY, _progress;
    bool _horizontal, _vertical, _threshold, _thresholdFired, _cancelled;
    TextView? _live;
    string? _override;
    global::Android.Animation.ValueAnimator? _anim;

    public SlideToConfirmView(Context context, string label, string confirm, System.Action commit, TextView? live = null, bool isPrimary = false) : base(context)
    {
        _label = label; _confirm = confirm; _commit = commit; _live = live; _isPrimary = isPrimary;
        _density = Resources!.DisplayMetrics!.Density;
        SetMinimumHeight((int)(64 * _density));
        ContentDescription = label; ImportantForAccessibility = ImportantForAccessibility.Yes;
        SetBackgroundColor(GColor.Transparent);
        Clickable = true;
    }
    float D(float n) => n * _density;
    float Sp(float n) => global::Android.Util.TypedValue.ApplyDimension(global::Android.Util.ComplexUnitType.Sp, n, Resources!.DisplayMetrics!);
    public override void OnInitializeAccessibilityNodeInfo(AccessibilityNodeInfo? info)
    {
        if (info == null) return;
        base.OnInitializeAccessibilityNodeInfo(info);
        info.ClassName = "android.widget.Button";
        info.ContentDescription = ContentDescription;
        info.AddAction(global::Android.Views.Accessibility.Action.Click);
    }
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        using var text = new GPaint { TextSize = Sp(_isPrimary ? 20 : 15) };
        var fm = text.GetFontMetrics();
        var desired = (int)Math.Max(D(64), (fm.Descent - fm.Ascent) + D(24));
        SetMeasuredDimension(GetDefaultSize(SuggestedMinimumWidth, widthMeasureSpec), ResolveSize(desired, heightMeasureSpec));
    }
    protected override void OnDraw(GCanvas canvas)
    {
        base.OnDraw(canvas);
        var w = Width; var h = Height; var thumb = D(56); var left = D(4) + _progress * Math.Max(0, w - thumb - D(8));
        using var track = new GPaint { AntiAlias = true, Color = GColor.ParseColor(_isPrimary ? "#F1EEE7" : "#212327") };
        canvas.DrawRoundRect(0, 0, w, h, D(18), D(18), track);
        using var fill = new GPaint { AntiAlias = true, Color = GColor.ParseColor("#393B40") };
        var fillRight = left + thumb / 2;
        canvas.DrawRoundRect(0, 0, fillRight, h, D(18), D(18), fill);
        using var text = new GPaint { AntiAlias = true, Color = GColor.ParseColor(_isPrimary ? "#0F1012" : "#F1EEE7"), TextSize = Sp(_isPrimary ? 20 : 15), TextAlign = GPaint.Align.Center, Alpha = _override != null || _progress >= .70f ? 255 : (int)(255 * Math.Max(.5, 1 - _progress / .70 * .5)) };
        text.SetTypeface(global::Android.Graphics.Typeface.Create("sans-serif", _isPrimary ? global::Android.Graphics.TypefaceStyle.Bold : global::Android.Graphics.TypefaceStyle.Normal));
        var shown = _override ?? (_progress >= .70f ? _confirm : _label);
        var maxWidth = Math.Max(0, w - D(80));
        while (shown.Length > 1 && text.MeasureText(shown) > maxWidth) shown = shown[..^2] + "…";
        var fm = text.GetFontMetrics();
        var baseline = h / 2f - (fm.Ascent + fm.Descent) / 2;
        canvas.Save(); canvas.ClipRect(0, 0, fillRight, h);
        text.Color = GColor.ParseColor("#F1EEE7");
        canvas.DrawText(shown, w / 2f, baseline, text);
        canvas.Restore();
        canvas.Save(); canvas.ClipRect(fillRight, 0, w, h);
        text.Color = GColor.ParseColor(_isPrimary ? "#0F1012" : "#F1EEE7");
        canvas.DrawText(shown, w / 2f, baseline, text);
        canvas.Restore();
        using var knob = new GPaint { AntiAlias = true, Color = GColor.ParseColor(_isPrimary ? "#0F1012" : "#F1EEE7") };
        canvas.DrawCircle(left + thumb / 2, h / 2f, thumb / 2, knob);
        using var arrow = new GPaint { AntiAlias = true, Color = GColor.ParseColor(_isPrimary ? "#F1EEE7" : "#0F1012"), TextSize = D(30), TextAlign = GPaint.Align.Center };
        canvas.DrawText("›", left + thumb / 2, h / 2f + D(10), arrow);
    }
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || !Enabled) return false;
        switch (e.Action)
        {
            case MotionEventActions.Down: _anim?.Cancel(); _downX = e.GetX(); _downY = e.GetY(); _progress = 0; _horizontal = _vertical = _threshold = _thresholdFired = _cancelled = false; return true;
            case MotionEventActions.Move:
                var dx = e.GetX() - _downX; var dy = e.GetY() - _downY;
                if (!_horizontal && !_vertical && Math.Max(Math.Abs(dx), Math.Abs(dy)) > ViewConfiguration.Get(Context!)!.ScaledTouchSlop) { if (Math.Abs(dy) > Math.Abs(dx)) _vertical = true; else { _horizontal = true; Parent?.RequestDisallowInterceptTouchEvent(true); } }
                if (_vertical) { _cancelled = true; return false; }
                if (_horizontal) { _progress = Math.Clamp(dx / Math.Max(1, Width - D(64)), 0, 1); var now = _progress >= .70f; if (now && !_thresholdFired) { _thresholdFired = true; PerformHapticFeedback(FeedbackConstants.VirtualKey); _live?.AnnounceForAccessibility(_confirm); } _threshold = now; Invalidate(); }
                return true;
            case MotionEventActions.Cancel: _cancelled = true; AnimateBack(); return true;
            case MotionEventActions.Up:
                if (_horizontal) { var done = !_cancelled && _progress >= .70f; if (done) { _progress = 0; PerformClick(); } else AnimateBack(); }
                else if (!_vertical && !_cancelled) { _progress = D(16) / Math.Max(1, Width - D(64)); Invalidate(); PostDelayed(() => { if (Enabled) AnimateBack(); }, 80); }
                return true;
        }
        return true;
    }
    void AnimateBack()
    {
        _anim?.Cancel(); var start = _progress;
        if (start <= 0) { Invalidate(); return; }
        var a = global::Android.Animation.ValueAnimator.OfFloat(0f, 1f)!;
        a.SetDuration(200);
        a.Update += (_, _) => { _progress = start * (1 - a.AnimatedFraction); Invalidate(); };
        _anim = a; a.Start();
    }
    public override bool PerformClick() { if (!Enabled) return false; base.PerformClick(); _commit(); return true; }
    public void SetStatusText(string? text) { _override = text; ContentDescription = text ?? _label; Invalidate(); }
    public void ResetProgress() { _anim?.Cancel(); _override = null; ContentDescription = _label; _progress = 0; Enabled = true; Invalidate(); }
    public void SetProgressFull() { _anim?.Cancel(); _progress = 1; Invalidate(); }
}