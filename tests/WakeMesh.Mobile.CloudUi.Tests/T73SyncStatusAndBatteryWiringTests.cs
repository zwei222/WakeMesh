using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T73: the device row showed "初期同期待ち" for every 0/0 row, including devices whose success
/// ACK was actually recorded server-side, and the Doze row on the diagnostics page had no button.
/// These probes pin the display contract onto the nullable server projection and audit that the
/// new button binds to members that really exist on DiagnosticsViewModel.
/// </summary>
public sealed class T73SyncStatusAndBatteryWiringTests
{
    private static DeviceListItem Row(long desired, long applied, bool? hasSuccessfulSync) =>
        new(Guid.NewGuid(), "Fold5", "Active", desired, applied, "active", false, null, hasSuccessfulSync);

    [Fact]
    public void AcknowledgedEmptyConfiguration_ShowsSyncedWithoutSettings()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, hasSuccessfulSync: true));

        Assert.Contains("同期済み", item.SyncStatusText);
        Assert.Contains("設定なし", item.SyncStatusText);
        Assert.DoesNotContain("初期同期待ち", item.SyncStatusText);
    }

    [Fact]
    public void ZeroRevisionsWithNoRecordedAck_KeepsPendingWording()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, hasSuccessfulSync: false));

        Assert.Equal("初期同期待ち", item.SyncStatusText);
    }

    [Fact]
    public void OldServerWithoutProjection_DoesNotFakeSuccess()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, hasSuccessfulSync: null));

        Assert.DoesNotContain("同期済み", item.SyncStatusText);
        Assert.Contains("初期同期待ち", item.SyncStatusText);
        Assert.Contains("未確認", item.SyncStatusText);
    }

    [Fact]
    public void ExistingConflictAndRetirementWording_IsPreserved()
    {
        var pending = new CloudDeviceItemViewModel(Row(5, 2, hasSuccessfulSync: true));
        Assert.Equal("反映待ち (最新 rev 5 / 適用 rev 2)", pending.SyncStatusText);

        var applied = new CloudDeviceItemViewModel(Row(5, 5, hasSuccessfulSync: false));
        Assert.Equal("適用報告済み (ACK済: rev 5)", applied.SyncStatusText);

        var retired = new CloudDeviceItemViewModel(
            new DeviceListItem(Guid.NewGuid(), "Fold5", "Retiring", 0, 0, "retired", false, null, true));
        Assert.Equal("退役処理中 (取消未確認)", retired.SyncStatusText);
    }

    [Fact]
    public void DiagnosticsPage_BatteryRow_HasOneButtonWiredToExistingViewModelMembers()
    {
        var xamlPath = Path.Combine(FindMobileRoot(), "Views", "DiagnosticsPage.xaml");
        var xaml = XDocument.Load(xamlPath);
        XNamespace ns = "http://schemas.microsoft.com/dotnet/2021/maui";

        var batteryLabel = xaml.Descendants(ns + "Label")
            .Single(e => (string?)e.Attribute("Text") == "省電力・Doze回避");
        var section = batteryLabel.Ancestors(ns + "Grid").First(); // the row Grid holding label + button

        var buttons = section.Elements(ns + "Button").ToList();
        var button = Assert.Single(buttons);

        Assert.Equal("{Binding BatteryActionText}", (string?)button.Attribute("Text"));
        Assert.Equal("{Binding OpenBatteryOptimizationSettingsCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding CanOpenBatteryOptimizationSettings}", (string?)button.Attribute("IsEnabled"));

        // Same visual hierarchy as the sibling permission rows on this page (no new design).
        Assert.Equal("12", (string?)button.Attribute("FontSize"));
        Assert.Equal("36", (string?)button.Attribute("HeightRequest"));
        Assert.Equal("8", (string?)button.Attribute("CornerRadius"));
        Assert.Equal("{StaticResource ChipBackground}", (string?)button.Attribute("BackgroundColor"));
        Assert.Equal("{StaticResource TextSlate100}", (string?)button.Attribute("TextColor"));

        // The row must reserve a second row for the button exactly like the other permission rows.
        var rowDefinitions = (string?)section.Attribute("RowDefinitions") ?? string.Empty;
        Assert.Equal(2, rowDefinitions.Split(',').Length);
    }

    [Fact]
    public void DiagnosticsPage_EveryBindingResolvesToAPublicViewModelMember()
    {
        var mobileRoot = FindMobileRoot();
        var pageText = File.ReadAllText(Path.Combine(mobileRoot, "Views", "DiagnosticsPage.xaml"));
        var vmSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "DiagnosticsViewModel.cs"));

        var bindingNames = Regex.Matches(pageText, @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("OpenBatteryOptimizationSettingsCommand", bindingNames);
        Assert.Contains("BatteryActionText", bindingNames);
        Assert.Contains("CanOpenBatteryOptimizationSettings", bindingNames);

        var unresolved = bindingNames
            .Where(name => !Regex.IsMatch(vmSource, $@"public\s+[^\r\n]{{0,160}}?\b{name}\b\s*(?:\{{|=>|\()"))
            .ToList();

        Assert.True(unresolved.Count == 0,
            "Bindings with no public member on DiagnosticsViewModel: " + string.Join(", ", unresolved));

        // The battery command must stay gated on the same predicate the button's IsEnabled reads.
        Assert.Matches(
            new Regex(@"OpenBatteryOptimizationSettingsCommand\s*=\s*new\s+AsyncRelayCommand\([\s\S]{0,600}?_ => !IsBusy && CanOpenBatteryOptimizationSettings"),
            vmSource);
    }

    private static string FindMobileRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "WakeMesh.Mobile");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
            candidate = Path.Combine(directory.FullName, "WakeMesh", "src", "WakeMesh.Mobile");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate src/WakeMesh.Mobile/App.xaml from test output.");
    }
}
