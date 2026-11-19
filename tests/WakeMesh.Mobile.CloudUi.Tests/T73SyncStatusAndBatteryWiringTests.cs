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
        // T88 copy-map: rev → バージョン (the numbers and their meaning are unchanged)
        Assert.Equal("反映待ち（最新バージョン5 / 適用済みバージョン2）", pending.SyncStatusText);

        var applied = new CloudDeviceItemViewModel(Row(5, 5, hasSuccessfulSync: false));
        // T88 copy-map: ACK/rev are internal values, the visible fact is the device's report
        Assert.Equal("端末から適用の報告あり（バージョン5）", applied.SyncStatusText);

        var retired = new CloudDeviceItemViewModel(
            new DeviceListItem(Guid.NewGuid(), "Fold5", "Retiring", 0, 0, "retired", false, null, true));
        // T88 copy-map: 退役 → 登録解除, 取消未確認 → 端末での解除待ち
        Assert.Equal("登録解除待ち（端末での解除待ち）", retired.SyncStatusText);
    }

    [Fact]
    public void DiagnosticsPage_BatteryRow_HasOneButtonWiredToExistingViewModelMembers()
    {
        var mobileRoot = FindMobileRoot();
        var xamlPath = Path.Combine(mobileRoot, "Views", "DiagnosticsPage.xaml");
        var xaml = XDocument.Load(xamlPath);
        XNamespace ns = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var batteryLabel = xaml.Descendants(ns + "Label")
            .Single(e => (string?)e.Attribute("Text") == "省電力・Doze回避");

        // The row is the VerticalStackLayout container that also holds the label and the state mark.
        var section = batteryLabel.Parent!;
        Assert.Equal("VerticalStackLayout", section.Name.LocalName);
        Assert.Contains(section.Elements(), e => e.Name.LocalName == "StateIndicator");

        var buttons = section.Elements(ns + "Button").ToList();
        var button = Assert.Single(buttons);

        Assert.Equal("{Binding BatteryActionText}", (string?)button.Attribute("Text"));
        Assert.Equal("{Binding OpenBatteryOptimizationSettingsCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding CanOpenBatteryOptimizationSettings}", (string?)button.Attribute("IsEnabled"));
        Assert.Equal("{Binding CanOpenBatteryOptimizationSettings}", (string?)button.Attribute("IsVisible"));
        Assert.Equal("{StaticResource SecondaryButtonStyle}", (string?)button.Attribute("Style"));

        // Same visual hierarchy as the sibling permission rows: the 48dp target comes from the row
        // style's MinimumHeightRequest, not from a new per-row design.
        var style = xaml.Descendants(ns + "Style")
            .Single(e => (string?)e.Attribute(x + "Key") == "SecondaryButtonStyle");
        var minHeight = style.Elements(ns + "Setter")
            .Single(e => (string?)e.Attribute("Property") == "MinimumHeightRequest");
        Assert.True(int.Parse((string)minHeight.Attribute("Value")!) >= 48);

        // The Doze entry guard stays: the button is only offered while the app is still optimized.
        var vmSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "DiagnosticsViewModel.cs"));
        Assert.Contains("CanOpenBatteryOptimizationSettings => _permissionService.OsApiLevel >= 23 && !IsBatteryOptimizationIgnored", vmSource);
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
