using System.Collections.Generic;

namespace WakeMesh.Mobile.Models;

public sealed record SoundOption(string Id, string DisplayName, string? Uri)
{
    public const string DefaultOsId = "os_default";
    public const string BuiltinChimeId = "builtin_chime";

    public static readonly SoundOption OsDefault = new(
        DefaultOsId,
        "OS既定アラーム音",
        null);

    public static readonly SoundOption BuiltinChime = new(
        BuiltinChimeId,
        "内蔵チャイム (オリジナル和音)",
        "wakemesh://sound/builtin_chime");

    public static IReadOnlyList<SoundOption> All { get; } = new[]
    {
        OsDefault,
        BuiltinChime
    };

    public static IReadOnlyList<SoundOption> AvailableOptions => All;

    public static SoundOption FromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri == OsDefault.Uri || uri == DefaultOsId)
        {
            return OsDefault;
        }
        if (uri == BuiltinChime.Uri || uri == BuiltinChimeId)
        {
            return BuiltinChime;
        }
        return OsDefault;
    }
}
