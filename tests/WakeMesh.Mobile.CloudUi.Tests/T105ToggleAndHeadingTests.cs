using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T105 source guards (T103 fix spec): the alarm-row Switch must bind one-way so a re-bind cannot
/// drive the inverting toggle, the code-behind must not write the model or call the toggle, and the
/// three readiness/ringing headings must lay the state mark and a wrapping label out in a Grid so the
/// text cannot clip at large font scales (t99-root.png showed the warn heading cut off).
/// </summary>
public sealed class T105ToggleAndHeadingTests
{
    private static readonly string Root = FindRoot();

    private const string AlarmsPagePath = "src/WakeMesh.Mobile/Views/AlarmsPage.xaml";
    private const string AlarmsPageCodeBehindPath = "src/WakeMesh.Mobile/Views/AlarmsPage.xaml.cs";

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath)).Replace("\r\n", "\n");

    private static string AlarmsPage => Source(AlarmsPagePath);

    private static XDocument AlarmsPageXml => XDocument.Load(Path.Combine(Root, AlarmsPagePath));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "WakeMesh.Mobile"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private const string RingingHeading = "アラームが鳴っています";
    private const string CriticalHeading = "時間どおりに鳴らない可能性があります";
    private const string WarnHeading = "アラームを鳴らすための設定が不足しています";

    private static XElement HeadingLabel(string text) =>
        AlarmsPageXml.Descendants()
            .Single(element => element.Name.LocalName == "Label" && (string?)element.Attribute("Text") == text);

    // --- Switch binding + handler ------------------------------------------

    [Fact]
    public void AlarmSwitchBindsIsEnabledOneWay()
    {
        var toggle = AlarmsPageXml.Descendants().Single(element => element.Name.LocalName == "Switch");

        Assert.Equal("{Binding IsEnabled, Mode=OneWay}", (string?)toggle.Attribute("IsToggled"));
        Assert.Equal("OnAlarmToggled", (string?)toggle.Attribute("Toggled"));
        Assert.Equal("1", (string?)toggle.Attribute("Grid.Column"));
    }

    [Fact]
    public void ToggleHandlerDelegatesToViewModelWithoutWritingTheModelOrToggling()
    {
        var behind = Source(AlarmsPageCodeBehindPath);

        Assert.Contains("OnAlarmToggled", behind);
        Assert.Contains("SetAlarmEnabledFromUserAsync(alarm,e.Value)", behind);
        Assert.DoesNotContain("alarm.IsEnabled=", behind);
        Assert.DoesNotContain("alarm.IsEnabled =", behind);
        Assert.DoesNotContain("ToggleAlarmAsync", behind);
    }

    // --- heading layout -----------------------------------------------------

    [Theory]
    [InlineData(RingingHeading, "critical", "Center")]
    [InlineData(CriticalHeading, "critical", "Center")]
    [InlineData(WarnHeading, "warn", "Center")]
    public void HeadingUsesGridWithWrappingLabelInColumnOne(string text, string stateKey, string verticalOptions)
    {
        var label = HeadingLabel(text);
        var grid = label.Parent;

        Assert.NotNull(grid);
        Assert.Equal("Grid", grid!.Name.LocalName);
        Assert.Equal("Auto,*", (string?)grid.Attribute("ColumnDefinitions"));
        Assert.Equal("7", (string?)grid.Attribute("ColumnSpacing"));

        // Label: column 1, wraps, keeps copy/size/weight/vertical alignment.
        Assert.Equal("1", (string?)label.Attribute("Grid.Column"));
        Assert.Equal("WordWrap", (string?)label.Attribute("LineBreakMode"));
        Assert.Equal("17", (string?)label.Attribute("FontSize"));
        Assert.Equal("Bold", (string?)label.Attribute("FontAttributes"));
        Assert.Equal(verticalOptions, (string?)label.Attribute("VerticalOptions"));

        // State mark: column 0 (default), same key, existing vertical alignment.
        var indicator = grid.Elements().First();
        Assert.Equal("StateIndicator", indicator.Name.LocalName);
        Assert.Equal(stateKey, (string?)indicator.Attribute("StateKey"));
        Assert.Null(indicator.Attribute("Grid.Column"));
        Assert.Equal(verticalOptions, (string?)indicator.Attribute("VerticalOptions"));
        Assert.Equal(2, grid.Elements().Count());
    }

    [Fact]
    public void NoHeadingStillUsesAHorizontalStackLayout()
    {
        var headingLayouts = AlarmsPageXml.Descendants()
            .Where(element => element.Name.LocalName == "HorizontalStackLayout")
            .Where(element => element.Elements().Any(child => child.Name.LocalName == "StateIndicator"))
            .ToList();

        Assert.Empty(headingLayouts);
        Assert.DoesNotContain("HorizontalStackLayout Spacing=\"7\"><controls:StateIndicator", AlarmsPage);
    }

    [Fact]
    public void HeadingCopyColorsAndBandStaysIntact()
    {
        // Colour of the band is still carried by the surrounding Border, so the labels stay 17sp bold
        // ink-coloured copy with no per-label colour override added by this fix.
        foreach (var text in new[] { RingingHeading, CriticalHeading, WarnHeading })
        {
            var label = HeadingLabel(text);
            Assert.Null(label.Attribute("TextColor"));
        }

        var indicators = AlarmsPageXml.Descendants().Where(e => e.Name.LocalName == "StateIndicator").ToList();
        Assert.Equal(3, indicators.Count);
        Assert.Equal(2, indicators.Count(i => (string?)i.Attribute("StateKey") == "critical"));
        Assert.Single(indicators, i => (string?)i.Attribute("StateKey") == "warn");
    }
}
