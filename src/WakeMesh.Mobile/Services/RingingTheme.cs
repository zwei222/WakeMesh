namespace WakeMesh.Mobile.Services;

// T194: 鳴動画面の背景テーマ。Id は保存値（英語、変更禁止）。表示名は Label。
public static class RingingThemes
{
    public const string Sunrise = "sunrise", Sky = "sky", Forest = "forest", Night = "night";
    public const string PreferenceKey = "ringing_background_theme";
    public static readonly IReadOnlyList<string> All = new[] { Sunrise, Sky, Forest, Night };
    public static string Normalize(string? id) => id is not null && All.Contains(id) ? id : Sunrise;
    public static string Label(string? id) => Normalize(id) switch { Sky => "青空", Forest => "木漏れ日", Night => "夜", _ => "朝焼け" };
}

public interface IRingingThemeStore { string Get(); void Set(string id); }

// 既定値。テストと、Android 以外で使う。
public sealed class InMemoryRingingThemeStore : IRingingThemeStore
{
    private string _id = RingingThemes.Sunrise;
    public string Get() => _id;
    public void Set(string id) => _id = RingingThemes.Normalize(id);
}

public sealed record RingingSlideColors(string Track, string Label, string Fill, string LabelOnFill, string Knob, string Arrow);

public sealed record RingingThemePalette(
    string Id, string Window, bool LightSystemBars,
    string BgTop, string BgMid, string BgBot,
    string GlowA, float AlphaA, float DAlphaA, string GlowB, float AlphaB, float DAlphaB,
    string Ink, string Ink2, string Ink3, string StatusInk,
    string Card, string CardStroke, string AllStopBg, string AllStopStroke, string AllStopText,
    RingingSlideColors Primary, RingingSlideColors Secondary)
{
    // 宣言の順序に注意: static field は書いた順に初期化されるので、LightPrimary を各テーマより先に置く。
    private static readonly RingingSlideColors LightPrimary = new("#0F1012", "#F1EEE7", "#F2766B", "#0F1012", "#F1EEE7", "#0F1012");

    public static readonly RingingThemePalette Sunrise = new(RingingThemes.Sunrise, "#FFF7EC", true,
        "#FFF7EC", "#FCEBDB", "#F7DDCB", "#FFC6A3", .50f, .06f, "#FFF1C8", .55f, -.05f,
        "#0F1012", "#5E5952", "#5E5952", "#A8433A",
        "#E0FFFDF9", "#E8D8C6", "#E0FFFDF9", "#F2766B", "#A8433A",
        LightPrimary, new("#EADFD2", "#0F1012", "#D6C3AE", "#0F1012", "#0F1012", "#F1EEE7"));

    public static readonly RingingThemePalette Sky = new(RingingThemes.Sky, "#F5FAFF", true,
        "#F5FAFF", "#E7F1FB", "#D8E7F5", "#FFFFFF", .50f, .06f, "#C6DDF4", .55f, -.05f,
        "#0F1012", "#4E5965", "#4E5965", "#A8433A",
        "#E0FBFDFF", "#CCDCEB", "#E0FBFDFF", "#F2766B", "#A8433A",
        LightPrimary, new("#DCE6F0", "#0F1012", "#B9CCDF", "#0F1012", "#0F1012", "#F1EEE7"));

    public static readonly RingingThemePalette Forest = new(RingingThemes.Forest, "#F7FAF0", true,
        "#F7FAF0", "#EAF2E0", "#DCE8D1", "#FFF2C2", .50f, .06f, "#CFE3BF", .55f, -.05f,
        "#0F1012", "#4F5A4A", "#4F5A4A", "#A8433A",
        "#E0FCFDF8", "#D2DFC8", "#E0FCFDF8", "#F2766B", "#A8433A",
        LightPrimary, new("#DFE8D5", "#0F1012", "#BFD0B0", "#0F1012", "#0F1012", "#F1EEE7"));

    public static readonly RingingThemePalette Night = new(RingingThemes.Night, "#0F1012", false,
        "#0F1012", "#111216", "#13151A", "#1C2230", .50f, .06f, "#1F1B1A", .55f, -.05f,
        "#F1EEE7", "#B7B3AA", "#8F8B83", "#F2766B",
        "#FF17181B", "#17181B", "#FF212327", "#F2766B", "#F2766B",
        new("#F1EEE7", "#0F1012", "#393B40", "#F1EEE7", "#0F1012", "#F1EEE7"),
        new("#212327", "#F1EEE7", "#393B40", "#F1EEE7", "#F1EEE7", "#0F1012"));

    public static RingingThemePalette For(string? id) => RingingThemes.Normalize(id) switch
    {
        RingingThemes.Sky => Sky, RingingThemes.Forest => Forest, RingingThemes.Night => Night, _ => Sunrise
    };
}
