using System.Globalization;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Models;
namespace WakeMesh.Mobile.Converters;
public sealed class NextFireTextConverter : IValueConverter
{
 public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) { if(value is not DateTimeOffset dto)return string.Empty; return $"次回 {dto.ToLocalTime().ToString("M月d日(ddd) H:mm",CultureInfo.GetCultureInfo("ja-JP"))}"; }
 public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)=>Binding.DoNothing;
}
public sealed class NullableDateVisibleConverter : IValueConverter
{
 public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)=>value is DateTimeOffset;
 public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)=>Binding.DoNothing;
}
public sealed class AlarmDaysTextConverter : IValueConverter
{
 public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)=>value is LocalAlarmModel alarm ? $"{alarm.Name} · {alarm.DaysSummary}" : string.Empty;
 public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)=>Binding.DoNothing;
}
public sealed class ReadinessLevelVisibleConverter : IValueConverter
{
 public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)=>string.Equals(value?.ToString(),parameter?.ToString(),StringComparison.OrdinalIgnoreCase);
 public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)=>Binding.DoNothing;
}
