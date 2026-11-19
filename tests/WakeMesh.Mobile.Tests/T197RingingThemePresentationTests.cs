using Xunit;

namespace WakeMesh.Mobile.Tests;

public sealed class T197RingingThemePresentationTests
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
    public void ThemeSelectionAndNativeBackdropAreWiredToTheApprovedPalette()
    {
        var root = Path.Combine(Root, "src/WakeMesh.Mobile");
        var control = File.ReadAllText(Path.Combine(root, "Controls/RingingThemeOption.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "Views/DiagnosticsPage.xaml"));
        var activity = File.ReadAllText(Path.Combine(root, "Platforms/Android/AlarmAlertActivity.cs"));
        var backdrop = File.ReadAllText(Path.Combine(root, "Platforms/Android/RingingBackdropView.cs"));
        Assert.Contains("ThemeIdProperty", control);
        Assert.Contains("SelectedThemeIdProperty", control);
        Assert.Contains("CommandProperty", control);
        Assert.Contains("sunrise", xaml);
        Assert.Contains("forest", xaml);
        Assert.Contains("次にアラームが鳴るときから適用されます", xaml);
        Assert.Contains("RingingThemePalette.For(SharedPreferencesRingingThemeStore.Read(this))", activity);
        Assert.Contains("_backdrop?.Start()", activity);
        Assert.Contains("_backdrop?.Stop()", activity);
        Assert.Contains("PeriodMs = 48000", backdrop);
        Assert.Contains("FrameMs = 66", backdrop);
        Assert.Contains("OnDetachedFromWindow", backdrop);
        Assert.Contains("RemoveCallbacksAndMessages(null)", backdrop);
    }
}
