using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T126GroupFooterEmptyStateTests
{
    [Fact]
    public void GroupOnlyStateSuppressesEmptyViewAndRetainsFooterAndLocalEmptyMessage()
    {
        var root = FindRoot();
        var document = XDocument.Load(Path.Combine(root, "src/WakeMesh.Mobile/Views/AlarmsPage.xaml"));
        var collection = document.Descendants().Single(element => element.Name.LocalName == "CollectionView"
            && (string?)element.Attribute("ItemsSource") == "{Binding Alarms}");
        var emptyView = collection.Elements().Single(element => element.Name.LocalName == "CollectionView.EmptyView");
        var trigger = collection.Elements().Single(element => element.Name.LocalName == "CollectionView.Triggers")
            .Elements().Single(element => element.Name.LocalName == "DataTrigger"
                && (string?)element.Attribute("Binding") == "{Binding HasGroupAlarms}"
                && (string?)element.Attribute("Value") == "True");
        Assert.Contains(trigger.Elements(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "EmptyView"
            && (string?)element.Attribute("Value") == "{x:Null}");
        Assert.Contains(emptyView.Descendants(), element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding EmptyTitle}");
        var footer = collection.Elements().Single(element => element.Name.LocalName == "CollectionView.Footer");
        Assert.Contains(footer.Descendants(), element => element.Name.LocalName == "VerticalStackLayout"
            && (string?)element.Attribute("BindableLayout.ItemsSource") == "{Binding GroupAlarms}");
        Assert.Contains(footer.Descendants(), element => element.Name.LocalName == "Label"
            && (string?)element.Attribute("Text") == "{Binding GroupSectionCaption}");
    }

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
