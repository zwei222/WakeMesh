using Xunit;

namespace WakeMesh.Mobile.Tests;

public sealed class T201RingingThemeLifecycleTests
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

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [Fact]
    public void SystemBarsAreAppliedOnlyAfterContentCreatesDecorView()
    {
        var activity = Read("src/WakeMesh.Mobile/Platforms/Android/AlarmAlertActivity.cs");
        var apply = activity.IndexOf("SetSystemBarsAppearance", StringComparison.Ordinal);
        var build = activity.IndexOf("BuildUi();", StringComparison.Ordinal);
        var buildDefinition = activity.IndexOf("private void BuildUi()", StringComparison.Ordinal);
        var content = activity.IndexOf("SetContentView(frame)", buildDefinition, StringComparison.Ordinal);
        Assert.True(build > 0 && buildDefinition > build && content > buildDefinition && apply > build,
            "BuildUi must install the content before requesting InsetsController.");
        Assert.Contains("Window?.InsetsController?.SetSystemBarsAppearance", activity);
    }

    [Fact]
    public void ActivityDoesNotDisposeBackdropBeforeAndroidDetachesIt()
    {
        var activity = Read("src/WakeMesh.Mobile/Platforms/Android/AlarmAlertActivity.cs");
        var destroy = activity.IndexOf("protected override void OnDestroy()", StringComparison.Ordinal);
        var end = activity.IndexOf("private void OnRingingChanged", destroy, StringComparison.Ordinal);
        var body = activity[destroy..end];
        Assert.Contains("_backdrop?.Stop()", body);
        Assert.DoesNotContain("_backdrop?.Dispose()", body);
    }

    [Fact]
    public void DetachStopsAnimationAndReleasesShadersWithoutDestroyingNativeView()
    {
        var backdrop = Read("src/WakeMesh.Mobile/Platforms/Android/RingingBackdropView.cs");
        var detach = backdrop.IndexOf("protected override void OnDetachedFromWindow()", StringComparison.Ordinal);
        var end = backdrop.IndexOf("protected override void Dispose", detach, StringComparison.Ordinal);
        var body = backdrop[detach..end];
        Assert.Contains("Stop();", body);
        Assert.Contains("ReleaseShader(_base)", body);
        Assert.Contains("ReleaseShader(_glowA)", body);
        Assert.Contains("ReleaseShader(_glowB)", body);
        Assert.DoesNotContain("Dispose(", body);
    }

    [Fact]
    public void DedicatedHandlerQueueIsClearedAndStartRemainsIdempotent()
    {
        var backdrop = Read("src/WakeMesh.Mobile/Platforms/Android/RingingBackdropView.cs");
        var start = backdrop.IndexOf("public void Start()", StringComparison.Ordinal);
        var stop = backdrop.IndexOf("public void Stop()", StringComparison.Ordinal);
        var tick = backdrop.IndexOf("void Tick()", StringComparison.Ordinal);
        var startBody = backdrop[start..stop];
        var stopBody = backdrop[stop..tick];
        Assert.Contains("if (_disposed || _running) return;", startBody);
        Assert.Contains("_tick.Post(_tickAction)", startBody);
        Assert.Contains("_tick.RemoveCallbacksAndMessages(null)", stopBody);
        Assert.DoesNotContain("RemoveCallbacks(_tickAction)", stopBody);
        Assert.Contains("PeriodMs = 48000", backdrop);
        Assert.Contains("FrameMs = 66", backdrop);
        Assert.Contains("readonly Handler _tick", backdrop);
    }
}
