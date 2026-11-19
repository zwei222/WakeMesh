using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Controls;

public sealed class RingingThemeOption : ContentView
{
    public static readonly BindableProperty ThemeIdProperty = BindableProperty.Create(nameof(ThemeId), typeof(string), typeof(RingingThemeOption), RingingThemes.Sunrise, propertyChanged: Changed);
    public static readonly BindableProperty SelectedThemeIdProperty = BindableProperty.Create(nameof(SelectedThemeId), typeof(string), typeof(RingingThemeOption), string.Empty, propertyChanged: Changed);
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(RingingThemeOption), null);
    public string ThemeId { get => (string)GetValue(ThemeIdProperty); set => SetValue(ThemeIdProperty, value); }
    public string SelectedThemeId { get => (string)GetValue(SelectedThemeIdProperty); set => SetValue(SelectedThemeIdProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public RingingThemeOption()
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { if (Command?.CanExecute(ThemeId) == true) Command.Execute(ThemeId); };
        GestureRecognizers.Add(tap);
        Rebuild();
    }
    static void Changed(BindableObject bindable, object oldValue, object newValue) => ((RingingThemeOption)bindable).Rebuild();

    void Rebuild()
    {
        var p = RingingThemePalette.For(ThemeId);
        var selected = ThemeId == SelectedThemeId;
        var thumbnail = new Grid { HeightRequest = 88, Background = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(p.BgTop), 0), new(Color.FromArgb(p.BgMid), .55f), new(Color.FromArgb(p.BgBot), 1) }, new Point(0, 0), new Point(0, 1)) };
        thumbnail.Children.Add(new BoxView { Background = Radial(p.GlowA, p.AlphaA, .80, .16), HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill, InputTransparent = true });
        thumbnail.Children.Add(new BoxView { Background = Radial(p.GlowB, p.AlphaB, .20, .64), HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill, InputTransparent = true });
        thumbnail.Children.Add(new Label { Text = "6:30", FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb(p.Ink), VerticalOptions = LayoutOptions.Start, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0), FontAutoScalingEnabled = false });
        var stop = new Border { BackgroundColor = Color.FromArgb(p.Primary.Track), StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 8 }, Padding = new Thickness(18, 3), VerticalOptions = LayoutOptions.End, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 0, 12), Content = new Label { Text = "停止", FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb(p.Primary.Label), HorizontalTextAlignment = TextAlignment.Center, FontAutoScalingEnabled = false } };
        thumbnail.Children.Add(stop);
        var check = new Border { WidthRequest = 22, HeightRequest = 22, Padding = 0, BackgroundColor = ResourceColor("Ink", Color.FromArgb("#F1EEE7")), Stroke = new SolidColorBrush(Color.FromArgb(p.BgTop)), StrokeThickness = 2, StrokeShape = new Ellipse(), HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start, Margin = 4, IsVisible = selected, Content = new Label { Text = "✓", FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = ResourceColor("OnInk", Color.FromArgb("#0F1012")), HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center } };
        thumbnail.Children.Add(check);
        var thumbBorder = new Border { StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 8 }, Content = thumbnail };
        var name = new Label { Text = RingingThemes.Label(ThemeId), FontSize = 15, FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None, TextColor = ResourceColor("Ink", Color.FromArgb("#F1EEE7")), HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 9) };
        var inner = new VerticalStackLayout { Spacing = 0 };
        inner.Children.Add(thumbBorder); inner.Children.Add(name);
        var outer = new Border { BackgroundColor = ResourceColor("Surface2", Color.FromArgb("#17181B")), Stroke = ResourceColor(selected ? "Ink" : "Line", Color.FromArgb(selected ? "#F1EEE7" : "#393B40")), StrokeThickness = selected ? 2 : 1, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Padding = selected ? 5 : 6, Content = inner };
        SemanticProperties.SetDescription(this, selected ? $"{RingingThemes.Label(ThemeId)}、選択中" : RingingThemes.Label(ThemeId));
        SemanticProperties.SetHint(this, selected ? null : "ダブルタップで選択");
        AutomationProperties.SetExcludedWithChildren(inner, true);
        Content = outer;
    }

    static RadialGradientBrush Radial(string hex, float alpha, double x, double y) => new()
    {
        Center = new Point(x, y), Radius = .75,
        GradientStops = new GradientStopCollection { new(Color.FromRgba(Color.FromArgb(hex).Red, Color.FromArgb(hex).Green, Color.FromArgb(hex).Blue, alpha), 0), new(Color.FromRgba(Color.FromArgb(hex).Red, Color.FromArgb(hex).Green, Color.FromArgb(hex).Blue, alpha * .45f), .5f), new(Color.FromRgba(Color.FromArgb(hex).Red, Color.FromArgb(hex).Green, Color.FromArgb(hex).Blue, 0), 1) }
    };
    static Color ResourceColor(string key, Color fallback) => Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color ? color : fallback;
}
