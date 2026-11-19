using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T142SlideViewWiringTests
{
    [Fact]
    public void NativeSlideView_guards_commits_and_exposes_minimal_status_contract()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/WakeMesh.Mobile/Platforms/Android/SlideToConfirmView.cs"));
        var source = File.ReadAllText(path);
        Assert.Contains("SetMinimumHeight(", source);
        Assert.DoesNotContain("MinimumHeight =", source);
        Assert.DoesNotContain("AccessibilityClassName =", source);
        Assert.Contains("OnInitializeAccessibilityNodeInfo", source);
        Assert.Contains("info.AddAction", source);
        Assert.Contains("public void SetStatusText(string? text)", source);
        Assert.Contains("public void ResetProgress()", source);
        Assert.Contains("public void SetProgressFull()", source);
        Assert.Contains("if (done) { _progress = 0; PerformClick(); }", source);
        Assert.DoesNotContain("PerformClick(); return true;\n        }", source);
        Assert.Contains("if (!Enabled) return false", source);
        Assert.Contains("_progress >= .70f", source);
    }
}
