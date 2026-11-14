using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Converters;

public sealed class SoundOptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        SoundOption.FromUri(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SoundOption option ? option.Id : SoundOption.DefaultOsId;
}
