using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T93: the diagnostics page used to paint every row with the same fixed "●" in Ink2, so "未許可",
/// "不要" and "許可済み" looked identical. The state marks are now the shared
/// Controls/StateIndicator driven by read-only state keys on the view model (T80 §4). This is a
/// source audit — it runs nothing and builds nothing.
/// </summary>
public sealed class T93DiagnosticsStateIndicatorTests
{
    private static readonly string Root = FindRoot();

    private static string Page => Path.Combine(Root, "src/WakeMesh.Mobile/Views/DiagnosticsPage.xaml");

    private static string ViewModelSource =>
        File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/ViewModels/DiagnosticsViewModel.cs"));

    private static XDocument PageXml => XDocument.Load(Page);

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

    private static IEnumerable<XElement> Indicators(XDocument xml)
    {
        foreach (var element in xml.Descendants())
        {
            if (element.Name.LocalName == "StateIndicator") yield return element;
        }
    }

    [Fact]
    public void DiagnosticsPageUsesTheSharedStateIndicatorForEveryStateRow()
    {
        var text = File.ReadAllText(Page);

        Assert.Contains("xmlns:controls=\"clr-namespace:WakeMesh.Mobile.Controls\"", text);

        var expected = new (string Key, string Text)[]
        {
            ("PermissionSummaryStateKey", "PermissionStatusSummary"),
            ("ExactAlarmStateKey", "ExactAlarmStatus"),
            ("NotificationStateKey", "NotificationStatus"),
            ("FullScreenStateKey", "FullScreenStatus"),
            ("BatteryStateKey", "BatteryStatus"),
            ("HolidayStateKey", "HolidayStatus")
        };

        var indicators = Indicators(PageXml).ToList();
        Assert.Equal(expected.Length, indicators.Count);

        foreach (var (key, statusText) in expected)
        {
            var indicator = Assert.Single(indicators, element => (string?)element.Attribute("StateKey") == $"{{Binding {key}}}");
            Assert.Equal($"{{Binding {statusText}}}", (string?)indicator.Attribute("Text"));
        }
    }

    [Fact]
    public void EveryDiagnosticsMarkIsStateDrivenAndNoFixedBulletSurvives()
    {
        var text = File.ReadAllText(Page);

        // The fixed "●" mark (same colour for granted, not granted and not required) is gone.
        Assert.DoesNotContain("●", text);
        // Copy-map: 鳴動 is replaced by アラームを鳴らす wording on visible source.
        Assert.DoesNotContain("鳴動", text);
        Assert.Contains("アラームを鳴らす準備", text);

        // Row labels stay exactly as the design fixed them.
        foreach (var label in new[] { "正確なアラーム", "通知", "全画面表示", "省電力・Doze回避", "祝日" })
            Assert.Contains($"Text=\"{label}\"", text);

        // No page-local colour decision: the control picks the token.
        Assert.DoesNotContain("#", text);

        var vm = ViewModelSource;
        foreach (var key in new[] { "ExactAlarmStateKey", "NotificationStateKey", "FullScreenStateKey", "BatteryStateKey", "PermissionSummaryStateKey", "HolidayStateKey" })
            Assert.Contains($"public string {key} =>", vm);
    }

    [Fact]
    public void DiagnosticsStateKeysAreReadOnlyAndUseTheSevenSpecKeys()
    {
        var vm = ViewModelSource;

        foreach (var property in new[] { "ExactAlarmStateKey", "NotificationStateKey", "FullScreenStateKey", "BatteryStateKey", "PermissionSummaryStateKey", "HolidayStateKey" })
        {
            // Expression-bodied getter only: the pages must not be able to set a state key.
            Assert.DoesNotContain($"{property} {{ get; set; }}", vm);
            Assert.Contains($"{property} =>", vm);
        }

        var page = File.ReadAllText(Page);
        var usedKeys = System.Text.RegularExpressions.Regex
            .Matches(page, @"StateKey=""\{Binding ([A-Za-z]+)\}""")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Groups[1].Value)
            .ToList();
        Assert.Equal(6, usedKeys.Count);

        Assert.Contains("\"ok\"", vm);
        Assert.Contains("\"critical\"", vm);
        Assert.Contains("\"warn\"", vm);
    }
}
