using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace WakeMesh.Mobile.Controls;

/// <summary>
/// T80 §4 state mark: one 12dp shape plus an optional short text, chosen only by the state key the
/// view models already expose (<c>StateKey</c> / <c>ReflectionStateKey</c>):
/// ok / pending / unknown / warn / critical / notRequired / inactive.
/// The shape and the colour are fixed by the design spec, so the pages never hard-code a colour.
/// The text is the caller's own wording; this control never invents one.
/// </summary>
public sealed class StateIndicator : ContentView
{
    private const double MarkSize = 12;

    public static readonly BindableProperty StateKeyProperty = BindableProperty.Create(
        nameof(StateKey),
        typeof(string),
        typeof(StateIndicator),
        "unknown",
        propertyChanged: OnVisualPropertyChanged);

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(StateIndicator),
        string.Empty,
        propertyChanged: OnVisualPropertyChanged);

    private readonly Grid _markHost;
    private readonly Label _text;

    public StateIndicator()
    {
        _markHost = new Grid
        {
            WidthRequest = MarkSize,
            HeightRequest = MarkSize,
            VerticalOptions = LayoutOptions.Center
        };

        _text = new Label
        {
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center,
            TextColor = ResourceColor("Ink2", Color.FromArgb("#B7B3AA"))
        };

        var root = new HorizontalStackLayout
        {
            Spacing = 7,
            VerticalOptions = LayoutOptions.Start
        };
        root.Add(_markHost);
        root.Add(_text);

        Content = root;
        Rebuild();
    }

    public string StateKey
    {
        get => GetValue(StateKeyProperty) as string ?? "unknown";
        set => SetValue(StateKeyProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty) as string ?? string.Empty;
        set => SetValue(TextProperty, value);
    }

    private static void OnVisualPropertyChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((StateIndicator)bindable).Rebuild();

    private void Rebuild()
    {
        var key = Normalize(StateKey);
        _markHost.Children.Clear();
        _markHost.Children.Add(CreateMark(key, ColorFor(key)));

        var text = Text;
        _text.Text = text;
        _text.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private static string Normalize(string? stateKey)
    {
        if (string.IsNullOrWhiteSpace(stateKey)) return "unknown";
        return stateKey.Trim().ToLowerInvariant() switch
        {
            "ok" => "ok",
            "pending" => "pending",
            "unknown" => "unknown",
            "warn" => "warn",
            "critical" => "critical",
            "notrequired" => "notRequired",
            "inactive" => "inactive",
            _ => "unknown"
        };
    }

    private static Color ColorFor(string key) => key switch
    {
        "ok" => ResourceColor("Ok", Color.FromArgb("#6CC290")),
        "pending" => ResourceColor("Ink2", Color.FromArgb("#B7B3AA")),
        "unknown" => ResourceColor("Ink2", Color.FromArgb("#B7B3AA")),
        "warn" => ResourceColor("Warn", Color.FromArgb("#E8A93A")),
        "critical" => ResourceColor("Crit", Color.FromArgb("#F2766B")),
        "notRequired" => ResourceColor("Ink3", Color.FromArgb("#8F8B83")),
        "inactive" => ResourceColor("Ink3", Color.FromArgb("#8F8B83")),
        _ => ResourceColor("Ink2", Color.FromArgb("#B7B3AA"))
    };

    private static View CreateMark(string key, Color color) => key switch
    {
        "ok" => new Ellipse
        {
            Fill = new SolidColorBrush(color),
            WidthRequest = MarkSize,
            HeightRequest = MarkSize
        },
        "pending" => new Ellipse
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            WidthRequest = 10.8,
            HeightRequest = 10.8,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        },
        "unknown" => new Ellipse
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            WidthRequest = 10.8,
            HeightRequest = 10.8,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        },
        "warn" => new Polygon
        {
            Points = new PointCollection
            {
                new Point(6, 1.2),
                new Point(11, 10.7),
                new Point(1, 10.7)
            },
            Fill = new SolidColorBrush(color),
            WidthRequest = MarkSize,
            HeightRequest = MarkSize,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start
        },
        "critical" => new Rectangle
        {
            Fill = new SolidColorBrush(color),
            WidthRequest = 8.2,
            HeightRequest = 8.2,
            Rotation = 45,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        },
        // T99: the "—" mark is a fixed 1x8 rule, not a glyph. A glyph grew with the user's font
        // size and stopped matching the other five 12dp marks, so the shape is drawn instead.
        "notRequired" => new BoxView
        {
            Color = color,
            WidthRequest = 8,
            HeightRequest = 1,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        },
        "inactive" => new BoxView
        {
            Color = color,
            WidthRequest = 8,
            HeightRequest = 2,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        },
        _ => new Ellipse
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            WidthRequest = 10.8,
            HeightRequest = 10.8,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        }
    };

    private static Color ResourceColor(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return fallback;
    }
}
