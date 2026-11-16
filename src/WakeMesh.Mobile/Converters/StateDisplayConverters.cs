using System;
using System.Collections;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace WakeMesh.Mobile.Converters;

/// <summary>
/// T80 §4: the short label that belongs to a state key. The keys themselves are owned by the view
/// models (<c>StateKey</c> / <c>ReflectionStateKey</c>) and are never rewritten here; this only
/// picks the fixed short wording that goes next to the mark. The full server wording stays in the
/// expandable details (SyncStatusText / ReflectionStatusText).
/// </summary>
public sealed class StateKeyToShortLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "ok" => "反映済み",
            "pending" => "反映待ち",
            "unknown" => "同期状況を確認できません",
            "warn" => "端末での解除待ち",
            "critical" => "対応が必要です",
            "notRequired" => "不要",
            "inactive" => "登録解除済み",
            _ => string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Empty-state visibility: true when the bound collection has no items, so the view can show a
/// caption instead of an empty picker. Display state only, never used for business decisions.
/// </summary>
public sealed class EmptyCollectionVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ICollection collection) return collection.Count == 0;
        if (value is int count) return count == 0;
        return value is null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
