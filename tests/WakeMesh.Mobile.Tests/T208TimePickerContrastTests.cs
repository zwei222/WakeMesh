using Xunit;

namespace WakeMesh.Mobile.Tests;

public sealed class T208TimePickerContrastTests
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
    public void AndroidTimePickerUsesStockDialogAndColorsBothButtonsAfterShow()
    {
        var handler = Read("src/WakeMesh.Mobile/Platforms/Android/WakeMeshTimePickerHandler.cs");
        Assert.Contains("class WakeMeshTimePickerHandler : TimePickerHandler", handler);
        Assert.Contains("base.CreateTimePickerDialog(hour, minute)", handler);
        Assert.Contains("ShowEvent +=", handler);
        Assert.Contains("ShowEvent -=", handler);
        Assert.Contains("GetButton(-1)", handler);
        Assert.Contains("GetButton(-2)", handler);
        Assert.Contains("SetTextColor", handler);
        Assert.DoesNotContain("SetButton(", handler);
        Assert.DoesNotContain("Hour", handler.Replace("hour", "", StringComparison.Ordinal));
    }

    [Fact]
    public void MauiProgramRegistersHandlerOnlyOnAndroidAndPreservesDisabledButtonState()
    {
        var handler = Read("src/WakeMesh.Mobile/Platforms/Android/WakeMeshTimePickerHandler.cs");
        var program = Read("src/WakeMesh.Mobile/MauiProgram.cs");
        Assert.Contains("GetColorForState", handler);
        Assert.Contains("StateEnabled", handler);
        Assert.Contains("AddHandler<TimePicker, Platforms.Android.WakeMeshTimePickerHandler>", program);
        var android = program.IndexOf("#if ANDROID", StringComparison.Ordinal);
        var registration = program.IndexOf("AddHandler<TimePicker, Platforms.Android.WakeMeshTimePickerHandler>", StringComparison.Ordinal);
        var end = program.IndexOf("#else", android, StringComparison.Ordinal);
        Assert.True(android >= 0 && registration > android && registration < end, "Handler registration must be Android-only.");
    }
}
