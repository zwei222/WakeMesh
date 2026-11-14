using System.Xml.Linq;
namespace WakeMesh.Mobile.CloudUi.Tests;
public sealed class T89CloudDesignWiringTests
{
    private static readonly string Root = FindRoot();
    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "src", "WakeMesh.Mobile"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    [Fact]
    public void CloudPageBindsCurrentRegistrationGroupsAndSafelyConfirmsDangerousActions()
    {
        var path = Path.Combine(Root, "src/WakeMesh.Mobile/Views/CloudManagementPage.xaml");
        var xaml = XDocument.Load(path);
        var text = File.ReadAllText(path);
        foreach (var binding in new[] { "NeedsConfigurationOrSignIn", "IsReauthRequired", "IsRecoveryNeeded", "RecoveryReason", "RegisterCurrentDeviceCommand", "ManualSyncCommand", "LastSyncStatusMessage", "FcmNoticeText", "HasUnappliedTargets", "ReflectionStateKey", "StateKey", "IsGroupListError", "IsDeviceListError" })
            Assert.Contains(binding, text);
        var code = File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Views/CloudManagementPage.xaml.cs"));
        foreach (var hook in new[] { "OnResetRecoveryClicked", "OnRetireClicked", "OnRevokeClicked", "OnGroupDeleteClicked", "ConfirmAsync", "CanExecute(parameter)", "Execute(parameter)" }) Assert.Contains(hook, code);
        // T91: T80 §3 ("⋮ を置き、DisplayActionSheet を開く。押せない項目はシートに出さない") makes the
        // action sheet the required menu. The earlier "no DisplayActionSheet" expectation contradicted
        // the spec, so it now pins the opposite: the menu is a real action sheet, gated per item.
        Assert.Contains("DisplayActionSheet", code);
        Assert.Contains("options.Count == 0", code);
        Assert.True(xaml.Descendants().Any(node => (string?)node.Attribute("BindingContext") == "{Binding}"));
    }

    [Fact]
    public void GroupEditorPreservesScheduleTargetControlsSoundMappingAndConflictConfirmation()
    {
        var path = Path.Combine(Root, "src/WakeMesh.Mobile/Views/CloudAlarmGroupEditPage.xaml");
        var text = File.ReadAllText(path);
        foreach (var binding in new[] { "ConflictMessage", "SaveOutcomeNotice", "DayMonday", "DayTuesday", "DayWednesday", "DayThursday", "DayFriday", "DaySaturday", "DaySunday", "TimeOfDay", "SoundOptionConverter", "Volume", "Vibrate", "Enabled", "ZeroVolumeVisibleConverter", "AddTargetCommand", "SaveCommand", "CancelCommand", "ErrorMessage" }) Assert.Contains(binding, text);
        var code = File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Views/CloudAlarmGroupEditPage.xaml.cs"));
        Assert.Contains("入力中の内容は上書きされます。", code);
        Assert.Contains("ReloadFromServerCommand", code);
        Assert.Contains("OnRemoveTargetClicked", code);
        // T91: the remove button stays on the code-behind handler so a failure reaches the fixed
        // error bar; the handler is what executes the view model command (checked here, not in XAML).
        Assert.Contains("RemoveTargetCommand", code);
        var app = XDocument.Load(Path.Combine(Root, "src/WakeMesh.Mobile/App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        Assert.Contains(app.Descendants().Select(node => (string?)node.Attribute(x + "Key")), key => key == "SoundOptionConverter");
        Assert.Contains("StringNotEmptyToBoolConverter", app.ToString());
        Assert.Contains("ZeroVolumeVisibleConverter", app.ToString());
        Assert.Contains("StateKeyToShortLabelConverter", app.ToString());
        Assert.Contains("EmptyCollectionVisibleConverter", app.ToString());
    }
}
