using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T132TimeAndSnoozeDisplayTests
{
    [Fact]
    public void SnoozeMinutesAndStepperAreDisplayedTogetherInCompactGrid()
    {
        var document = LoadPage("AlarmEditPage.xaml");
        var stepper = document.Descendants().Single(element => element.Name.LocalName == "Stepper"
            && (string?)element.Attribute("Value") == "{Binding SnoozeMinutes}");
        var grid = stepper.Parent!;
        Assert.Equal("*,Auto,Auto", (string?)grid.Attribute("ColumnDefinitions"));
        Assert.Contains(grid.Elements(), element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding SnoozeMinutes,StringFormat='{0}分'}"
            && (string?)element.Attribute("Grid.Column") == "1");
        Assert.Equal("2", (string?)stepper.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)stepper.Attribute("Minimum"));
        Assert.Equal("30", (string?)stepper.Attribute("Maximum"));
        Assert.Equal("1", (string?)stepper.Attribute("Increment"));
    }

    [Fact]
    public void LocalAlarmRowsUseAnUnambiguous24HourTimeFormat()
    {
        var document = LoadPage("AlarmsPage.xaml");
        var timeLabel = document.Descendants().Single(element => element.Name.LocalName == "Label"
            && ((string?)element.Attribute("Text"))?.StartsWith("{Binding Time,") == true);
        var binding = (string)timeLabel.Attribute("Text")!;
        Assert.Contains("{0:HH\\\\:mm}", binding);
        Assert.DoesNotContain("{0:hh\\\\:mm}", binding);
        var sourceFormat = binding.Split("{0:", StringSplitOptions.None)[1].Split("}")[0];
        var runtimeFormat = sourceFormat.Replace("\\\\:", ":", StringComparison.Ordinal);

        foreach (var example in new[] { (0, "00:00"), (6, "06:00"), (12, "12:00"), (18, "18:00"), (23, "23:59") })
        {
            var time = new TimeOnly(example.Item1, example.Item1 == 23 ? 59 : 0);
            Assert.Equal(example.Item2, time.ToString(runtimeFormat, CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void AlarmListGroupFooterEmptyViewAndRefreshBindingsRemainIntact()
    {
        var document = LoadPage("AlarmsPage.xaml");
        var collection = document.Descendants().Single(element => element.Name.LocalName == "CollectionView"
            && (string?)element.Attribute("ItemsSource") == "{Binding Alarms}");
        Assert.Contains(collection.Elements().Single(element => element.Name.LocalName == "CollectionView.Triggers")
            .Descendants(), element => element.Name.LocalName == "DataTrigger"
            && (string?)element.Attribute("Binding") == "{Binding HasGroupAlarms}"
            && element.Elements().Any(setter => setter.Name.LocalName == "Setter"
                && (string?)setter.Attribute("Property") == "EmptyView"
                && (string?)setter.Attribute("Value") == "{x:Null}"));
        var footer = collection.Elements().Single(element => element.Name.LocalName == "CollectionView.Footer");
        Assert.Contains(footer.Descendants(), element => element.Name.LocalName == "VerticalStackLayout"
            && (string?)element.Attribute("BindableLayout.ItemsSource") == "{Binding GroupAlarms}");
        Assert.Contains(footer.Descendants(), element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding GroupSectionCaption}");
        var refresh = document.Descendants().Single(element => element.Name.LocalName == "RefreshView");
        Assert.Equal("{Binding IsBusy, Mode=OneWay}", (string?)refresh.Attribute("IsRefreshing"));
        Assert.Equal("{Binding LoadAlarmsCommand}", (string?)refresh.Attribute("Command"));
        var localSwitch = document.Descendants().Single(element => element.Name.LocalName == "Switch"
            && (string?)element.Attribute("Toggled") == "OnAlarmToggled");
        Assert.Equal("{Binding IsEnabled, Mode=OneWay}", (string?)localSwitch.Attribute("IsToggled"));
    }

    private static XDocument LoadPage(string fileName) => XDocument.Load(Path.Combine(FindRoot(), "src/WakeMesh.Mobile/Views", fileName));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "WakeMesh.Mobile"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
