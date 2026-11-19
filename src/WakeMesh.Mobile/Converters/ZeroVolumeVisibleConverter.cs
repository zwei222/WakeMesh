using System;
using System.Globalization;
using Microsoft.Maui.Controls;
namespace WakeMesh.Mobile.Converters;
public sealed class ZeroVolumeVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int volume && volume == 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
