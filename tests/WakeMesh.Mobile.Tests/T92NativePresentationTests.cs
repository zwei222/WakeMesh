using Xunit;

namespace WakeMesh.Mobile.Tests;

public sealed class T92NativePresentationTests
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
    public void NativeAlertChangesPresentationWithoutChangingAlarmActions()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Platforms/Android/AlarmAlertActivity.cs"));
        foreach (var presentation in new[] { "#0F1012", "#17181B", "#212327", "#F1EEE7", "#F2766B", "Text = \"アラーム\"", "TextSize = 22", "Dp(64)", "SetStroke(Dp(2)", "GetRingingAlarms().Count >= 2" })
            Assert.Contains(presentation, source);
        foreach (var preserved in new[] { "ActionDismissAll", "ActionDismiss", "ActionSnooze", "PutExtra(\"alarm_id\"", "PutExtra(\"snooze_minutes\"", "StartService(serviceIntent)", "Finish();", "LongClick +=" })
            Assert.Contains(preserved, source);
        Assert.DoesNotContain("#0B0F19", source);
        Assert.DoesNotContain("#38BDF8", source);
        Assert.DoesNotContain("SetPadding(48, 64, 48, 64)", source);
    }
}
