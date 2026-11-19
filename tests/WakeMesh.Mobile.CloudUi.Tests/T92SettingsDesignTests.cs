using System.Xml.Linq;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T92SettingsDesignTests
{
    private static readonly string Root = FindRoot();

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
    public void DiagnosticsHubHasAllFunctionalSettingsAndNoFalseCloudState()
    {
        var page = Path.Combine(Root, "src/WakeMesh.Mobile/Views/DiagnosticsPage.xaml");
        var xml = XDocument.Load(page);
        var text = File.ReadAllText(page);
        foreach (var required in new[] { "PermissionStatusSummary", "ExactAlarmStatus", "NotificationStatus", "FullScreenStatus", "BatteryStatus", "CanRequestNotification", "OpenBatteryOptimizationSettingsCommand", "HolidayStatus", "HolidayCoverageNotice", "RefreshHolidaysCommand", "AuthStatus", "DeviceId", "RescheduledMessage" })
            Assert.Contains(required, text);
        Assert.Contains("省電力・Doze回避", text);
        Assert.Contains("OnAccountTapped", text);
        Assert.DoesNotContain("Azure同期", text);
        Assert.DoesNotContain("ローカル単独運用中", text);
        // No text control pins a fixed HeightRequest; only MinimumHeightRequest (>=48dp) is allowed.
        foreach (var element in xml.Descendants())
        {
            if (element.Name.LocalName is "Label" or "Button" or "Entry" or "Editor" or "Span")
                Assert.Null(element.Attribute("HeightRequest"));
        }
        Assert.DoesNotContain("🎌", text);
        Assert.NotNull(xml.Root);

        var code = File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Views/DiagnosticsPage.xaml.cs"));
        Assert.Contains("Resumed += OnWindowResumed", code);
        Assert.Contains("RefreshCommand", code);
        Assert.Contains("Resumed -= OnWindowResumed", code);
        Assert.Contains("GoToAsync(nameof(SettingsPage))", code);
    }

    [Fact]
    public void AccountPageConfirmsCredentialDestructionAndKeepsSettingsCommandsWired()
    {
        var page = Path.Combine(Root, "src/WakeMesh.Mobile/Views/SettingsPage.xaml");
        var xaml = XDocument.Load(page);
        var text = File.ReadAllText(page);
        foreach (var field in new[] { "ApiBaseUrl", "AndroidClientId", "ApiClientId", "ValidationMessage", "SaveSettingsCommand", "SignInCommand", "CanSignIn", "CheckConnectionCommand", "ConnectionCheckResultText", "IsRebuildNeeded", "CompileTimeClientId" })
            Assert.Contains(field, text);
        // The credential-destruction button is confirmed in code-behind, so the page wires a handler
        // and the handler runs ClearSettingsCommand — that is the actual command wiring, not XAML text.
        Assert.Contains("Clicked=\"OnClearSettingsClicked\"", text);
        Assert.Contains("MaximumWidthRequest=\"640\"", text);
        Assert.Contains("MinimumHeightRequest=\"48\"", text);
        Assert.NotNull(xaml.Root);
        var code = File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Views/SettingsPage.xaml.cs"));
        Assert.Contains("_viewModel.ClearSettingsCommand.CanExecute(null)", code);
        Assert.Contains("_viewModel.ClearSettingsCommand.Execute(null)", code);
        foreach (var phrase in new[] { "サインアウトしますか", "接続設定を消去しますか", "この端末のアラームは削除されません", "CanExecute(null)", "Execute(null)" })
            Assert.Contains(phrase, code);
    }
}
