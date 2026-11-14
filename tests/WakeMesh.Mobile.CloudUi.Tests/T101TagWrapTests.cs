using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T101 regression guard for the Themis S1 follow-up (t100 followup queue): the alarm-row tag strip
/// must wrap inside the left column instead of clipping. The real AlarmsPage.xaml is asserted, and the
/// T99 fixes around this container (640dp column, add-button row span, single ringing stop, band state
/// marks, 40sp non-scaling time, one footer) must stay untouched.
/// </summary>
public sealed class T101TagWrapTests
{
    private static readonly string Root = FindRoot();

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath)).Replace("\r\n", "\n");

    private const string AlarmsPagePath = "src/WakeMesh.Mobile/Views/AlarmsPage.xaml";

    private static string AlarmsPage => Source(AlarmsPagePath);
    private static XDocument AlarmsPageXml => XDocument.Load(Path.Combine(Root, AlarmsPagePath));

    private static IEnumerable<XElement> Descendants(string localName) =>
        AlarmsPageXml.Descendants().Where(element => element.Name.LocalName == localName);

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

    private static XElement TagRow() =>
        Descendants("FlexLayout").Single(flex => (string?)flex.Attribute("Grid.Row") == "3"
                                                  && (string?)flex.Attribute("Grid.Column") == "0");

    private static XElement Tag(string text) =>
        TagRow().Elements().Single(label => (string?)label.Attribute("Text") == text);

    private const string RegistrationWarning = "端末にアラームを設定できていません";
    private const string CoverageWarning = "祝日判定の範囲外（アラームは鳴ります）";
    private const string HolidayTag = "祝日は休み";

    // --- S1: the tag strip wraps inside the left column --------------------

    [Fact]
    public void TagStripIsAWrappingFlexLayoutInTheLeftColumn()
    {
        var flex = TagRow();

        Assert.Equal("Row", (string?)flex.Attribute("Direction"));
        Assert.Equal("Wrap", (string?)flex.Attribute("Wrap"));
        Assert.Equal("Center", (string?)flex.Attribute("AlignItems"));
        Assert.Equal("Start", (string?)flex.Attribute("AlignContent"));
        Assert.Equal("Start", (string?)flex.Attribute("JustifyContent"));
        Assert.Equal("0,2,0,0", (string?)flex.Attribute("Margin"));

        // The spec keeps the tags inside .meta: no spanning into the switch / menu columns.
        Assert.Null(flex.Attribute("Grid.ColumnSpan"));
        Assert.DoesNotContain("Grid.ColumnSpan", flex.ToString());
    }

    [Fact]
    public void TagStripReplacesTheClippingHorizontalStackLayout()
    {
        Assert.DoesNotContain("HorizontalStackLayout Grid.Row=\"3\" Grid.Column=\"0\"", AlarmsPage);
        Assert.DoesNotContain("Spacing=\"10\"", AlarmsPage);
    }

    [Fact]
    public void EveryTagUsesTheTwelveDpGapMargin()
    {
        foreach (var text in new[] { RegistrationWarning, CoverageWarning, HolidayTag })
        {
            Assert.Equal("0,0,12,0", (string?)Tag(text).Attribute("Margin"));
        }

        Assert.Equal(3, TagRow().Elements().Count());
    }

    [Fact]
    public void WarningTagsWrapAndMayShrink_WithoutMaxLines()
    {
        foreach (var text in new[] { RegistrationWarning, CoverageWarning })
        {
            var label = Tag(text);

            Assert.Equal("WordWrap", (string?)label.Attribute("LineBreakMode"));
            Assert.Equal("1", (string?)label.Attribute("FlexLayout.Shrink"));
            Assert.Null(label.Attribute("MaxLines"));
        }
    }

    [Fact]
    public void ShortTagStaysOnOneLineAndDoesNotShrink()
    {
        var label = Tag(HolidayTag);

        Assert.Equal("NoWrap", (string?)label.Attribute("LineBreakMode"));
        Assert.Equal("0", (string?)label.Attribute("FlexLayout.Shrink"));
        Assert.Null(label.Attribute("MaxLines"));
    }

    [Fact]
    public void TagCopyColorsVisibilityBindingsAndOrderAreUnchanged()
    {
        var labels = TagRow().Elements().ToList();

        Assert.Equal(new[] { RegistrationWarning, CoverageWarning, HolidayTag },
            labels.Select(l => (string?)l.Attribute("Text")).ToArray());

        var registration = labels[0];
        Assert.Equal("{Binding IsRegistrationFailed}", (string?)registration.Attribute("IsVisible"));
        Assert.Equal("{StaticResource Crit}", (string?)registration.Attribute("TextColor"));
        Assert.Equal("13", (string?)registration.Attribute("FontSize"));

        var coverage = labels[1];
        Assert.Equal("{Binding HasHolidayCoverageWarning}", (string?)coverage.Attribute("IsVisible"));
        Assert.Equal("{StaticResource Warn}", (string?)coverage.Attribute("TextColor"));
        Assert.Equal("13", (string?)coverage.Attribute("FontSize"));

        var holiday = labels[2];
        Assert.Equal("{Binding SkipHolidays}", (string?)holiday.Attribute("IsVisible"));
        Assert.Equal("{StaticResource Ink2}", (string?)holiday.Attribute("TextColor"));
        Assert.Equal("13", (string?)holiday.Attribute("FontSize"));
    }

    // --- T99 behaviour that must survive the container swap -----------------

    [Fact]
    public void T99AlarmListColumnAndAddButtonSpanSurvive()
    {
        var grid = AlarmsPageXml.Root!.Elements().Single(element => element.Name.LocalName == "Grid");
        Assert.Equal("640", (string?)grid.Attribute("MaximumWidthRequest"));
        Assert.Equal("Center", (string?)grid.Attribute("HorizontalOptions"));

        var add = Descendants("Button").Single(b => (string?)b.Attribute("Text") == "＋ 追加");
        Assert.Equal("0", (string?)add.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)add.Attribute("Grid.RowSpan"));

        var time = Descendants("Label").Single(l => (string?)l.Attribute("Grid.Row") == "0"
                                                    && (string?)l.Attribute("Grid.Column") == "0"
                                                    && (string?)l.Attribute("FontSize") == "40");
        Assert.Equal("False", (string?)time.Attribute("FontAutoScalingEnabled"));

        Assert.Equal(1, Descendants("Label").Count(l => (string?)l.Attribute("Text") == "{Binding StatusMessage}"));
    }

    [Fact]
    public void T99SingleRingingStopAndBandMarksSurvive()
    {
        var single = Descendants("Button").Single(b => (string?)b.Attribute("Text") == "停止"
                                                       && (string?)b.Attribute("MinimumHeightRequest") == "56");
        Assert.Equal("{Binding DismissAllRingingCommand}", (string?)single.Attribute("Command"));

        var stopAll = Descendants("Button").Single(b => (string?)b.Attribute("Text") == "すべて停止");
        Assert.Equal("{Binding DismissAllRingingCommand}", (string?)stopAll.Attribute("Command"));

        var marks = Descendants("StateIndicator")
            .Select(i => (string?)i.Attribute("StateKey"))
            .Where(key => key is not null)
            .ToList();
        Assert.Equal(3, marks.Count);
        Assert.Equal(2, marks.Count(key => key == "critical"));
        Assert.Single(marks.Where(key => key == "warn"));
    }
}
