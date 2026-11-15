using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T116GroupAlarmWiringTests
{
    private static readonly string Root = FindRoot();
    private static string Xaml => File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Views/AlarmsPage.xaml"));
    private static string Code => File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Views/AlarmsPage.xaml.cs"));
    private static XDocument Xml => XDocument.Parse(Xaml);
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

    [Fact]
    public void PullRefreshIsOneWayAndKeepsTheExistingLoadCommand()
    {
        var refresh = Xml.Descendants().Single(element => element.Name.LocalName == "RefreshView");
        Assert.Equal("{Binding IsBusy, Mode=OneWay}", (string?)refresh.Attribute("IsRefreshing"));
        Assert.Equal("{Binding LoadAlarmsCommand}", (string?)refresh.Attribute("Command"));
    }

    [Fact]
    public void GroupSectionIsInFooterBeforeTheSingleExistingStatus()
    {
        var footer = Xml.Descendants().Single(element => element.Name.LocalName == "CollectionView.Footer");
        var group = footer.Descendants().Single(element => element.Name.LocalName == "VerticalStackLayout"
            && (string?)element.Attribute("BindableLayout.ItemsSource") == "{Binding GroupAlarms}");
        var status = footer.Descendants().Single(element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding StatusMessage}");
        Assert.True(group.Ancestors().First(element => element.Name.LocalName == "VerticalStackLayout")
            .Elements().TakeWhile(element => element != status).Any());
        Assert.Single(Xml.Descendants().Where(element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding StatusMessage}"));
    }

    [Fact]
    public void GroupRowsAreReadOnlyAndBindOnlyRowData()
    {
        var template = Xml.Descendants().Single(element => element.Name.LocalName == "DataTemplate"
            && (string?)element.Attribute(XName.Get("DataType", "http://schemas.microsoft.com/winfx/2009/xaml")) == "cloud:GroupAlarmRow");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "Switch");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "TapGestureRecognizer"
            && element.Attribute("Toggled") is not null);
        Assert.Contains(template.Descendants().OfType<XElement>(), element => element.Name.LocalName == "Span"
            && (string?)element.Attribute("Text") == "{Binding GroupName}");
        Assert.Contains(template.Descendants().OfType<XElement>(), element => element.Name.LocalName == "Span"
            && (string?)element.Attribute("Text") == "{Binding DaysText}");
        Assert.Contains(template.Descendants().OfType<XElement>(), element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding WarningText}");
        Assert.Contains(template.Descendants().OfType<XElement>(), element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding PendingText}");
        Assert.DoesNotContain("#", template.ToString());
    }

    [Fact]
    public void PageWiresSyncLifecycleAndUsesOnlyTypedGroupRows()
    {
        Assert.Contains("SubscribeSyncEvents()", Code);
        Assert.Contains("UnsubscribeSyncEvents()", Code);
        Assert.Contains("is not GroupAlarmRow row", Code);
        Assert.Contains("OpenGroupCommand.ExecuteAsync(row)", Code);
        Assert.Contains("グループの編集にはサインインが必要です", Code);
        Assert.Contains("このアラームはここでは変更できません", Code);
        Assert.Contains("EmptyTitle", Xaml);
    }
}
