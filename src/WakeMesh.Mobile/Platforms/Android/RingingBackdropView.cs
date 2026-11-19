using Android.Content;
using Android.OS;
using Android.Views;
using WakeMesh.Mobile.Services;
using GColor = global::Android.Graphics.Color;
using GPaint = global::Android.Graphics.Paint;
using GCanvas = global::Android.Graphics.Canvas;
using GMatrix = global::Android.Graphics.Matrix;
using GShader = global::Android.Graphics.Shader;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class RingingBackdropView : global::Android.Views.View
{
    const long PeriodMs = 48000, FrameMs = 66;
    readonly RingingThemePalette _p;
    readonly Handler _tick = new(Looper.MainLooper!);
    readonly GPaint _base = new() { Dither = true }, _glowA = new() { Dither = true }, _glowB = new() { Dither = true };
    readonly GMatrix _ma = new(), _mb = new();
    readonly Action _tickAction;
    bool _running, _disposed;
    long _startMs;
    float _phase0, _phase;

    public RingingBackdropView(Context context, RingingThemePalette palette) : base(context)
    {
        _p = palette;
        _tickAction = Tick;
        Clickable = false;
        LongClickable = false;
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        ReleaseShader(_base);
        ReleaseShader(_glowA);
        ReleaseShader(_glowB);
        if (w <= 0 || h <= 0) return;
        try
        {
            _base.SetShader(new global::Android.Graphics.LinearGradient(0, 0, 0, h,
                new[] { GColor.ParseColor(_p.BgTop).ToArgb(), GColor.ParseColor(_p.BgMid).ToArgb(), GColor.ParseColor(_p.BgBot).ToArgb() },
                new[] { 0f, .55f, 1f }, GShader.TileMode.Clamp!));
            _glowA.SetShader(Glow(_p.GlowA, .95f * w));
            _glowB.SetShader(Glow(_p.GlowB, .85f * w));
        }
        catch
        {
            ReleaseShader(_base); ReleaseShader(_glowA); ReleaseShader(_glowB);
            _base.Color = GColor.ParseColor(_p.Window);
        }
        Invalidate();
    }

    static global::Android.Graphics.RadialGradient Glow(string hex, float r) => new(0, 0, r,
        new[] { GColor.ParseColor("#FF" + hex[1..]).ToArgb(), GColor.ParseColor("#73" + hex[1..]).ToArgb(), GColor.ParseColor("#00" + hex[1..]).ToArgb() },
        new[] { 0f, .5f, 1f }, GShader.TileMode.Clamp!);

    protected override void OnDraw(GCanvas canvas)
    {
        base.OnDraw(canvas);
        if (_base.Shader is null) { canvas.DrawColor(GColor.ParseColor(_p.Window)); return; }
        var w = Width; var h = Height;
        var s = (float)Math.Sin(2 * Math.PI * _phase);
        var c = (float)Math.Cos(2 * Math.PI * _phase);
        canvas.DrawPaint(_base);
        DrawGlow(canvas, _glowA, _ma, w * (.80f + .06f * s), h * (.16f + .025f * c), _p.AlphaA + _p.DAlphaA * s);
        DrawGlow(canvas, _glowB, _mb, w * (.20f - .05f * s), h * (.64f - .03f * c), _p.AlphaB + _p.DAlphaB * s);
    }

    static void DrawGlow(GCanvas canvas, GPaint paint, GMatrix matrix, float x, float y, float alpha)
    {
        if (paint.Shader is null) return;
        matrix.SetTranslate(x, y);
        paint.Shader.SetLocalMatrix(matrix);
        paint.Alpha = (int)(Math.Clamp(alpha, 0, 1) * 255);
        canvas.DrawPaint(paint);
    }

    public void Start()
    {
        if (_disposed || _running) return;
        try
        {
            var power = (PowerManager?)Context?.GetSystemService(Context.PowerService);
            if (!global::Android.Animation.ValueAnimator.AreAnimatorsEnabled() || power?.IsPowerSaveMode == true)
            { _phase0 = _phase = 0; Invalidate(); return; }
        }
        catch { _phase0 = _phase = 0; Invalidate(); return; }
        _running = true;
        _startMs = SystemClock.UptimeMillis();
        _tick.Post(_tickAction);
    }

    public void Stop()
    {
        if (_running) { _phase0 = _phase; }
        _running = false;
        _tick.RemoveCallbacksAndMessages(null);
    }

    void Tick()
    {
        if (!_running || _disposed) { Stop(); return; }
        _phase = (_phase0 + (SystemClock.UptimeMillis() - _startMs) / (float)PeriodMs) % 1f;
        Invalidate();
        _tick.PostDelayed(_tickAction, FrameMs);
    }

    protected override void OnDetachedFromWindow()
    {
        Stop();
        ReleaseShader(_base); ReleaseShader(_glowA); ReleaseShader(_glowB);
        base.OnDetachedFromWindow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Stop();
            ReleaseShader(_base); ReleaseShader(_glowA); ReleaseShader(_glowB);
            _base.Dispose(); _glowA.Dispose(); _glowB.Dispose();
            _ma.Dispose(); _mb.Dispose(); _tick.Dispose();
        }
        base.Dispose(disposing);
    }

    static void ReleaseShader(GPaint paint)
    {
        var shader = paint.Shader;
        paint.SetShader(null);
        shader?.Dispose();
    }
}
