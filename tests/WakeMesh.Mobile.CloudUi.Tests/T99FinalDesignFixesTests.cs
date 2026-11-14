using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T99 regression guards for the final design fixes that Themis approved (t87 canonical):
/// the 640dp column on the alarm list, the single-alarm stop button, the band state marks, the
/// outcome-driven sync mark, the font-independent "not required" mark, the .png shell icons and the
/// IsWeekly lifecycle subscription. Old T88/T89/T91/T92/T93 guards stay as they are; this file only
/// adds the missing ones and runs the real view model.
/// </summary>
public sealed class T99FinalDesignFixesTests
{
    private static readonly string Root = FindRoot();

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath)).Replace("\r\n", "\n");

    private static string AlarmsPagePath => "src/WakeMesh.Mobile/Views/AlarmsPage.xaml";
    private static string AlarmsPage => Source(AlarmsPagePath);
    private static XDocument AlarmsPageXml => XDocument.Load(Path.Combine(Root, AlarmsPagePath));

    private static string CloudPagePath => "src/WakeMesh.Mobile/Views/CloudManagementPage.xaml";
    private static string CloudPage => Source(CloudPagePath);

    private static string CloudVmSource => Source("src/WakeMesh.Mobile/ViewModels/CloudManagementViewModel.cs");
    private static string StateIndicatorSource => Source("src/WakeMesh.Mobile/Controls/StateIndicator.cs");
    private static string AppShellSource => Source("src/WakeMesh.Mobile/AppShell.xaml");
    private static string AlarmEditBehind => Source("src/WakeMesh.Mobile/Views/AlarmEditPage.xaml.cs");

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

    private static XElement RootGrid() =>
        AlarmsPageXml.Root!.Elements().Single(element => element.Name.LocalName == "Grid");

    private static IEnumerable<XElement> Descendants(string localName) =>
        AlarmsPageXml.Descendants().Where(element => element.Name.LocalName == localName);

    // --- 1) alarm list column and the add button overlap -------------------

    [Fact]
    public void AlarmListColumnIsCappedAt640AndCentered()
    {
        var grid = RootGrid();

        Assert.Equal("640", (string?)grid.Attribute("MaximumWidthRequest"));
        Assert.Equal("Center", (string?)grid.Attribute("HorizontalOptions"));
    }

    [Fact]
    public void AddButtonStillSpansBothRowsSoItCannotOverlapTheListCopy()
    {
        var add = Descendants("Button").Single(b => (string?)b.Attribute("Text") == "＋ 追加");

        Assert.Equal("0", (string?)add.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)add.Attribute("Grid.RowSpan"));
    }

    [Fact]
    public void AlarmTimeStays40AndIgnoresFontScaling_AndTheListHasOneFooterOnly()
    {
        var time = Descendants("Label").Single(l => (string?)l.Attribute("Grid.Row") == "0"
                                                    && (string?)l.Attribute("Grid.Column") == "0"
                                                    && (string?)l.Attribute("FontSize") == "40");

        Assert.Equal("False", (string?)time.Attribute("FontAutoScalingEnabled"));

        // Exactly one status footer: the duplicate count label that used to sit under the empty view is gone.
        var footers = Descendants("Label").Count(l => (string?)l.Attribute("Text") == "{Binding StatusMessage}");
        Assert.Equal(1, footers);
        Assert.Single(Descendants("CollectionView.Footer"));
        Assert.Single(Descendants("CollectionView.EmptyView"));
        Assert.DoesNotContain("アラームの件数", AlarmsPage);
    }

    // --- 2) single ringing alarm: one full-width stop ----------------------

    [Fact]
    public void SingleRingingAlarmShowsOneFullWidthStopButton()
    {
        var single = Descendants("Button").Single(b => (string?)b.Attribute("Text") == "停止"
                                                       && (string?)b.Attribute("MinimumHeightRequest") == "56");

        Assert.Equal("{Binding DismissAllRingingCommand}", (string?)single.Attribute("Command"));
        Assert.Equal("False", (string?)single.Attribute("IsVisible"));

        var trigger = single.Elements().Single(e => e.Name.LocalName == "Button.Triggers").Elements().Single();
        Assert.Equal("DataTrigger", trigger.Name.LocalName);
        Assert.Equal("{Binding RingingAlarms.Count}", (string?)trigger.Attribute("Binding"));
        Assert.Equal("1", (string?)trigger.Attribute("Value"));
        Assert.Equal("True", (string?)trigger.Elements().Single().Attribute("Value"));
    }

    [Fact]
    public void SingleRingingAlarmHidesThePerAlarmListAndTheStopAllButton()
    {
        var list = Descendants("CollectionView").Single(c => (string?)c.Attribute("ItemsSource") == "{Binding RingingAlarms}");
        var trigger = list.Elements().Single(e => e.Name.LocalName == "CollectionView.Triggers").Elements().Single();
        Assert.Equal("{Binding RingingAlarms.Count}", (string?)trigger.Attribute("Binding"));
        Assert.Equal("1", (string?)trigger.Attribute("Value"));
        var setter = trigger.Elements().Single();
        Assert.Equal("IsVisible", (string?)setter.Attribute("Property"));
        Assert.Equal("False", (string?)setter.Attribute("Value"));

        var stopAll = Descendants("Button").Single(b => (string?)b.Attribute("Text") == "すべて停止");
        Assert.Equal("{Binding DismissAllRingingCommand}", (string?)stopAll.Attribute("Command"));
        var allTrigger = stopAll.Elements().Single(e => e.Name.LocalName == "Button.Triggers").Elements().Single();
        Assert.Equal("{Binding RingingAlarms.Count}", (string?)allTrigger.Attribute("Binding"));
        Assert.Equal("1", (string?)allTrigger.Attribute("Value"));

        // The per-alarm command and the multi-alarm wording survive untouched.
        Assert.Contains("DismissRingingAlarmCommand", AlarmsPage);
        Assert.Contains("すべて停止", AlarmsPage);
    }

    [Fact]
    public void EachBandHeadlineCarriesTheSharedTwelveDpMarkInTheApprovedColour()
    {
        Assert.Contains("xmlns:controls=\"clr-namespace:WakeMesh.Mobile.Controls\"", AlarmsPage);

        var marks = Descendants("StateIndicator").ToList();
        Assert.Equal(3, marks.Count);
        Assert.Equal(2, marks.Count(m => (string?)m.Attribute("StateKey") == "critical"));
        Assert.Single(marks, m => (string?)m.Attribute("StateKey") == "warn");

        // The marks sit beside the headline copy, not below it, and carry no page-local colour.
        // T105: the wrapper is a two-column Grid (mark = Auto, copy = * and wrapping) so the long
        // Japanese headlines cannot clip; the copy, size, weight and mark colour are unchanged.
        Assert.Contains("<Grid ColumnDefinitions=\"Auto,*\" ColumnSpacing=\"7\"><controls:StateIndicator StateKey=\"critical\" VerticalOptions=\"Center\"/><Label Grid.Column=\"1\" Text=\"アラームが鳴っています\"", AlarmsPage);
        Assert.Contains("<Grid ColumnDefinitions=\"Auto,*\" ColumnSpacing=\"7\"><controls:StateIndicator StateKey=\"critical\" VerticalOptions=\"Center\"/><Label Grid.Column=\"1\" Text=\"時間どおりに鳴らない可能性があります\"", AlarmsPage);
        Assert.Contains("<Grid ColumnDefinitions=\"Auto,*\" ColumnSpacing=\"7\"><controls:StateIndicator StateKey=\"warn\" VerticalOptions=\"Center\"/><Label Grid.Column=\"1\" Text=\"アラームを鳴らすための設定が不足しています\"", AlarmsPage);
        Assert.DoesNotContain("TextColor=\"#", AlarmsPage);
    }

    // --- 3) sync mark follows the outcome ----------------------------------

    [Fact]
    public void CloudSyncBandBindsTheOutcomeStateKeyInsteadOfAFixedKey()
    {
        Assert.Contains("<controls:StateIndicator StateKey=\"{Binding LastSyncStateKey}\" Text=\"{Binding LastSyncStatusMessage}\" />", CloudPage);
        Assert.DoesNotContain("StateKey=\"pending\"", CloudPage);

        Assert.Contains("public string LastSyncStateKey", CloudVmSource);
        // Read-only from the page: the setter is private and the default is the neutral key.
        Assert.Contains("private set => OnUiThread(() => SetProperty(ref _lastSyncStateKey, value));", CloudVmSource);
        Assert.Contains("_lastSyncStateKey = \"unknown\"", CloudVmSource);
    }

    [Theory]
    [InlineData(DesiredSyncStatus.AppliedAcknowledged, "ok")]
    [InlineData(DesiredSyncStatus.ApplyFailedAcknowledged, "critical")]
    [InlineData(DesiredSyncStatus.ApplyIncompleteNoAck, "critical")]
    [InlineData(DesiredSyncStatus.TransportFailed, "warn")]
    [InlineData(DesiredSyncStatus.CanceledNoAck, "unknown")]
    [InlineData(DesiredSyncStatus.StaleRevisionSkipped, "unknown")]
    public async Task SyncStateKeyIsDerivedFromTheRealOutcome(DesiredSyncStatus status, string expected)
    {
        var service = new StubCloudManagementService(Task.FromResult(Outcome(status)));
        var vm = NewViewModel(service);

        await vm.InitializeAsync();

        Assert.Equal(expected, vm.LastSyncStateKey);
        Assert.False(string.IsNullOrWhiteSpace(vm.LastSyncStatusMessage));
        Assert.NotEqual("同期中...", vm.LastSyncStatusMessage);
    }

    [Fact]
    public async Task SyncStateKeyIsPendingWhileTheCycleIsInFlight()
    {
        var gate = new TaskCompletionSource<DesiredSyncOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubCloudManagementService(gate.Task);
        var vm = NewViewModel(service);

        var running = vm.InitializeAsync();
        await service.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("pending", vm.LastSyncStateKey);
        Assert.Equal("同期中...", vm.LastSyncStatusMessage);

        gate.SetResult(Outcome(DesiredSyncStatus.AppliedAcknowledged));
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("ok", vm.LastSyncStateKey);
    }

    // --- 4) state mark is font-size independent ---------------------------

    [Fact]
    public void NotRequiredMarkIsAOneByEightRuleNotAGlyph()
    {
        var marks = StateIndicatorSource[StateIndicatorSource.IndexOf("private static View CreateMark", StringComparison.Ordinal)..];
        var branch = Branch(marks, "\"notRequired\" =>");

        Assert.Contains("new BoxView", branch);
        Assert.Contains("WidthRequest = 8", branch);
        Assert.Contains("HeightRequest = 1", branch);
        Assert.DoesNotContain("FontSize", branch);
        Assert.DoesNotContain("—", branch);
    }

    [Fact]
    public void MarkHostCentersAgainstItsTextAndTheOtherSixShapesAreUnchanged()
    {
        Assert.Contains("HeightRequest = MarkSize,\n            VerticalOptions = LayoutOptions.Center", StateIndicatorSource);

        Assert.Equal(4, Count(StateIndicatorSource, "new Ellipse"));     // ok, pending, unknown, default
        Assert.Equal(1, Count(StateIndicatorSource, "new Polygon"));     // warn
        Assert.Equal(1, Count(StateIndicatorSource, "new Rectangle"));   // critical
        Assert.Equal(2, Count(StateIndicatorSource, "new BoxView"));     // notRequired + inactive
        Assert.Contains("private const double MarkSize = 12;", StateIndicatorSource);
    }

    // --- 5) shell icons and the weekly lifecycle --------------------------

    [Fact]
    public void ShellTabIconsUseTheGeneratedPngNames()
    {
        Assert.Equal(3, Count(AppShellSource, ".png\""));
        Assert.DoesNotContain(".svg\"", AppShellSource);
        foreach (var icon in new[] { "clock.png", "cloud.png", "settings.png" })
        {
            Assert.Contains($"Icon=\"{icon}\"", AppShellSource);
        }
    }

    [Fact]
    public void AlarmEditPageTracksIsWeeklyForThePageLifetimeWithoutWritingBindings()
    {
        Assert.Contains("_viewModel.PropertyChanged += OnViewModelPropertyChanged;", AlarmEditBehind);
        Assert.Contains("_viewModel.PropertyChanged -= OnViewModelPropertyChanged;", AlarmEditBehind);
        Assert.Contains("e.PropertyName == nameof(AlarmEditViewModel.IsWeekly)", AlarmEditBehind);

        // Subscribed when the page appears, released when it disappears.
        var appearing = Between(AlarmEditBehind, "protected override void OnAppearing()", "protected override void OnSizeAllocated");
        var disappearing = AlarmEditBehind[AlarmEditBehind.IndexOf("protected override void OnDisappearing()", StringComparison.Ordinal)..];
        Assert.Contains("+= OnViewModelPropertyChanged", appearing);
        Assert.Contains("-= OnViewModelPropertyChanged", disappearing);

        // The layout callback only reads state; it never assigns the bound weekday flags.
        var layout = Between(AlarmEditBehind, "private void UpdateWeekdayLayout", "private void OnToggleMonday");
        Assert.DoesNotContain("SetValue", layout);
        Assert.DoesNotContain("IsWeekly =", layout);

        // The once/weekly handlers and the size callback that the design kept are still there.
        Assert.Contains("private void OnSelectOnce(object? sender, EventArgs e)", AlarmEditBehind);
        Assert.Contains("private void OnSelectWeekly(object? sender, EventArgs e)", AlarmEditBehind);
        Assert.Contains("protected override void OnSizeAllocated(double width, double height)", AlarmEditBehind);
    }

    // --- helpers ---------------------------------------------------------

    private static DesiredSyncOutcome Outcome(DesiredSyncStatus status) =>
        new(status, null, null, null, null);

    private static CloudManagementViewModel NewViewModel(ICloudManagementService service) =>
        new(service, new MockAuthService("https://api.wakemesh.local", "token"));

    private static string Branch(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, marker);
        var end = text.IndexOf("},", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return text[start..end];
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Cloud service double: one registration, empty lists, and a gate-able sync outcome.</summary>
    private sealed class StubCloudManagementService : ICloudManagementService
    {
        private readonly Task<DesiredSyncOutcome> _sync;

        public StubCloudManagementService(Task<DesiredSyncOutcome> sync) => _sync = sync;

        public TaskCompletionSource<bool> SyncStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConfiguredAndSignedIn => true;
        public string? CurrentUserAccountId => "test-owner-account-id";

        public event EventHandler? RegistrationChanged;
        public event EventHandler? SyncCompleted;

        public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudDeviceRegistration?>(new CloudDeviceRegistration
            {
                ServerDeviceId = Guid.NewGuid(),
                RegistrationGeneration = 1,
                ApiBaseUrl = "https://api.wakemesh.local",
                AccountId = "test-owner-account-id",
                DeviceDisplayName = "Fold5"
            });

        public Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(
            string displayName, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Network, message: "stub"));

        public Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<DeviceListItem>>.Ok(Array.Empty<DeviceListItem>()));

        public Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(Array.Empty<AlarmGroupDto>()));

        public Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
            AlarmGroupWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<GroupSaveOutcome>.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
            string groupId, AlarmGroupWriteRequest request, long ifMatchVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<GroupSaveOutcome>.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult> DeleteAlarmGroupAsync(
            string groupId, long ifMatchVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default)
        {
            SyncStarted.TrySetResult(true);
            return _sync;
        }
    }
}
