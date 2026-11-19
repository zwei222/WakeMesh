using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T118 behavioural tests: after the real <see cref="MobileDesiredApplier"/> has applied a desired snapshot,
/// the alarm list shows the cloud rows in their own section, the manual list stays manual-only, sync events
/// refresh the projection without rescheduling anything, and no cloud row can reach a manual edit / toggle /
/// delete path.
/// </summary>
public sealed class T118GroupAlarmListTests : IDisposable
{
    private static readonly Guid DeviceId = Guid.Parse("cccccccc-3333-4333-8333-cccccccccccc");
    private static readonly Guid ScheduleA = Guid.Parse("11111111-aaaa-4aaa-8aaa-111111111111");
    private static readonly Guid ScheduleB = Guid.Parse("22222222-bbbb-4bbb-8bbb-222222222222");

    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly MockAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly CloudSyncBinding _binding;
    private readonly FileCloudSyncManifestStore _manifestStore;
    private readonly MobileDesiredApplier _applier;
    private readonly FileCloudDeviceRegistrationStore _registrationStore;
    private readonly GroupAlarmSource _groupSource;
    private readonly DesiredObservationStore _observations;
    private readonly T118CloudStub _cloud;
    private readonly List<string> _navigatedRoutes = new();

    public T118GroupAlarmListTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t118_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _scheduler = new MockAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(_holidayService), _scheduler);

        _binding = new CloudSyncBinding("https://api.wakemesh.local", "owner-account", DeviceId, 1);
        _manifestStore = new FileCloudSyncManifestStore(
            _binding, CloudSyncStoragePaths.Manifest(_tempDir, _binding));
        _applier = new MobileDesiredApplier(_binding, _coordinator, _manifestStore);

        _registrationStore = new FileCloudDeviceRegistrationStore(Path.Combine(_tempDir, "registration.json"));
        _groupSource = new GroupAlarmSource(_registrationStore, _tempDir);
        _observations = new DesiredObservationStore();
        _cloud = new T118CloudStub();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // --- helpers -----------------------------------------------------------

    private async Task SaveRegistrationAsync()
    {
        await _registrationStore.SaveRegistrationAsync(new CloudDeviceRegistration(
            DeviceId, 1, _binding.ApiBaseUrl, _binding.AccountId, "Fold5"));
    }

    private AlarmListViewModel NewViewModel(IGroupAlarmSource? source = null)
    {
        var vm = new AlarmListViewModel(
            _coordinator,
            _holidayService,
            new LocalDeviceInfoProvider("t118-device"),
            cloud: _cloud,
            observations: _observations,
            groupSource: source ?? _groupSource,
            navigation: new ActionCloudNavigation(route => _navigatedRoutes.Add(route)));

        vm.SubscribeSyncEvents();   // the page owns this lifecycle call
        return vm;
    }

    private static DesiredItemDto Item(
        Guid scheduleId,
        string groupId = "group-1",
        string label = "グループ朝",
        long modifiedRevision = 10,
        string time = "07:00") =>
        new(scheduleId, groupId, label, true, "weekly", new[] { 1, 2, 3, 4, 5 }, null, "Asia/Tokyo",
            true, Array.Empty<string>(), time, "sound-default", 80, true, true, modifiedRevision);

    private DesiredSnapshotDto Snapshot(
        IEnumerable<DesiredItemDto>? items = null,
        IEnumerable<DesiredTombstoneDto>? tombstones = null,
        long revision = 1) =>
        new(DeviceId, revision, Guid.NewGuid(),
            items?.ToList() ?? new List<DesiredItemDto>(),
            tombstones?.ToList() ?? new List<DesiredTombstoneDto>());

    private async Task<LocalAlarmModel> SaveManualAlarmAsync(
        string id = "manual-t118",
        string name = "手動アラーム",
        bool enabled = true)
    {
        var alarm = new LocalAlarmModel
        {
            Id = id,
            Name = name,
            IsEnabled = enabled,
            Time = new TimeOnly(6, 30),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
            DeviceId = "t118-device",
            TimeZoneId = "Asia/Tokyo"
        };
        await _coordinator.SaveAndScheduleAsync(alarm);
        return alarm;
    }

    // --- 1) the real applier puts group alarms in their own section --------

    [Fact]
    public async Task AfterRealApply_ManualListIsManualOnly_AndGroupRowCarriesNameDaysAndNextFire()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync();

        var applied = await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));
        Assert.True(applied.Succeeded);

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        var manual = Assert.Single(vm.Alarms);
        Assert.Equal("manual-t118", manual.Id);

        var row = Assert.Single(vm.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Equal("グループ朝", row.GroupName);
        Assert.Equal("平日 (月〜金)", row.DaysText);          // wire 1..5 = Monday..Friday
        Assert.Equal("07:00", row.TimeText);
        Assert.NotNull(row.NextFireText);
        Assert.Equal("group-1", row.GroupId);
        Assert.True(row.CanOpenGroup);

        Assert.True(vm.HasGroupAlarms);
        Assert.Equal("グループのアラーム", vm.GroupSectionCaption);
        Assert.Equal("1 件のアラーム (1 件有効)・グループのアラーム 1 件", vm.StatusMessage);
        Assert.True(vm.CanEditCloudGroups);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ManualOnlyList_KeepsTheExactHistoricalFooterWording()
    {
        await SaveManualAlarmAsync();

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        Assert.Single(vm.Alarms);
        Assert.Empty(vm.GroupAlarms);
        Assert.False(vm.HasGroupAlarms);
        Assert.Equal("1 件のアラーム (1 件有効)", vm.StatusMessage);
        Assert.Equal("アラームはまだありません", vm.EmptyTitle);
    }

    [Fact]
    public async Task OnlyCloudAlarms_ProduceTheNoManualAlarmsEmptyTitle()
    {
        await SaveRegistrationAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        Assert.Empty(vm.Alarms);
        Assert.Single(vm.GroupAlarms);
        Assert.Equal("この端末で作ったアラームはありません", vm.EmptyTitle);
    }

    // --- 2) legacy (non-prefixed) manifest ids -----------------------------

    [Fact]
    public async Task LegacyManifestId_IsGroupedOnScreen_AndCannotBeToggledOrDeletedAsManual()
    {
        await SaveRegistrationAsync();
        var legacy = await SaveManualAlarmAsync("legacy-cloud-alarm", "旧グループ");
        var manifest = await _manifestStore.LoadAsync();
        manifest.Schedules[ScheduleA] = new SyncScheduleEntry(ScheduleA, legacy.Id, "group-legacy", 5);
        await _manifestStore.SaveAsync(manifest);

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        Assert.Empty(vm.Alarms);
        var row = Assert.Single(vm.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Equal("legacy-cloud-alarm", row.LocalAlarmId);

        var schedulesBefore = _scheduler.ScheduledAlarms.Count;
        var cancelsBefore = _scheduler.CancelledAlarmIds.Count;

        Assert.False(await vm.SetAlarmEnabledFromUserAsync(legacy, false));
        Assert.False(await vm.ToggleAlarmAsync(legacy.Id));
        Assert.False(await vm.DeleteAlarmAsync(legacy.Id));

        var stored = await _store.GetByIdAsync(legacy.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.IsEnabled);
        Assert.Equal(schedulesBefore, _scheduler.ScheduledAlarms.Count);
        Assert.Equal(cancelsBefore, _scheduler.CancelledAlarmIds.Count);
    }

    [Fact]
    public async Task SyncPrefixedId_IsRefusedEvenWithoutASource()
    {
        var alarm = await SaveManualAlarmAsync("manual-t118");
        var vm = new AlarmListViewModel(_coordinator, _holidayService, new LocalDeviceInfoProvider("t118-device"));
        await vm.LoadAlarmsAsync();

        var cloudOwned = new LocalAlarmModel
        {
            Id = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, ScheduleA),
            Name = "グループ朝"
        };
        Assert.False(await vm.SetAlarmEnabledFromUserAsync(cloudOwned, false));
        Assert.False(await vm.ToggleAlarmAsync(cloudOwned.Id));
        Assert.False(await vm.DeleteAlarmAsync(cloudOwned.Id));

        // the manual alarm still works exactly as before
        Assert.True(await vm.SetAlarmEnabledFromUserAsync(alarm, false));
    }

    // --- 3) observation-driven Pending rows --------------------------------

    [Fact]
    public async Task Offline_WithNoObservation_ShowsTheAppliedRowsAndClaimsNothingAboutPending()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        Assert.Null(_observations.Latest);
        var row = Assert.Single(vm.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.Null(row.PendingText);
        Assert.Null(row.WarningText);
    }

    [Fact]
    public async Task CorruptManifest_StillListsTheAppliedRows_ButWithoutAGroupLink()
    {
        await SaveRegistrationAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        // Corrupt the manifest on disk: the applier would refuse it, and the list must not hide what is
        // already scheduled on this device.
        await File.WriteAllTextAsync(CloudSyncStoragePaths.Manifest(_tempDir, _binding), "{ this is not json");

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        var row = Assert.Single(vm.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudApplied, row.Origin);
        Assert.False(row.CanOpenGroup);
        Assert.Null(row.GroupId);
        Assert.Contains(GroupAlarmProjector.ManifestUnreadableWarningText, row.WarningText);
    }

    [Fact]
    public async Task ProjectionFailure_FromABackgroundRefresh_IsReportedSafely()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync();

        var cloud = _cloud;
        var vm = new AlarmListViewModel(
            _coordinator,
            _holidayService,
            new LocalDeviceInfoProvider("t118-device"),
            cloud: cloud,
            observations: _observations,
            groupSource: new ThrowingGroupAlarmSource(),
            navigation: new ActionCloudNavigation(route => _navigatedRoutes.Add(route)));
        vm.SubscribeSyncEvents();

        cloud.RaiseSyncCompleted();
        await vm.WaitForPendingProjectionAsync();

        Assert.Equal("グループのアラームを更新できませんでした", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    private sealed class ThrowingGroupAlarmSource : IGroupAlarmSource
    {
        public Task<GroupAlarmSourceResult> LoadAsync(System.Threading.CancellationToken cancellationToken = default) =>
            throw new IOException("manifest directory vanished");
    }

    [Fact]
    public async Task ObservationOfThisBinding_CreatesAPendingRowForAnUnappliedSchedule()
    {
        await SaveRegistrationAsync();
        // Real apply of schedule A only: the manifest/binding exist, schedule B is still "not applied here".
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        _observations.Publish(
            _binding.ToStorageKey(),
            Snapshot(new[] { Item(ScheduleA), Item(ScheduleB, groupId: "group-2", label: "あとで", time: "05:00") }, revision: 2),
            DesiredSyncStatus.AppliedAcknowledged,
            DateTimeOffset.UtcNow);

        await vm.WaitForPendingProjectionAsync();

        var pending = Assert.Single(vm.GroupAlarms, row => row.Origin == GroupAlarmOrigin.CloudPending);
        Assert.Equal("あとで", pending.GroupName);
        Assert.Null(pending.NextFireText);
        Assert.Equal(GroupAlarmProjector.PendingWaitingText, pending.PendingText);
        Assert.Equal(GroupAlarmProjector.PendingEnabledText, pending.EnabledText);
        Assert.Equal("05:00", pending.TimeText);
    }

    [Fact]
    public async Task ObservationOfAnotherBinding_CreatesNoPendingRow()
    {
        await SaveRegistrationAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        _observations.Publish(
            "another-binding-key",
            Snapshot(new[] { Item(ScheduleB) }),
            DesiredSyncStatus.AppliedAcknowledged,
            DateTimeOffset.UtcNow);
        await vm.WaitForPendingProjectionAsync();

        Assert.DoesNotContain(vm.GroupAlarms, row => row.Origin == GroupAlarmOrigin.CloudPending);
        Assert.Single(vm.GroupAlarms);
    }

    // --- 4) sync notifications refresh only, never reschedule --------------

    [Fact]
    public async Task SyncCompleted_RefreshesTheProjection_WithoutSchedulingOrBusy()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();
        Assert.Single(vm.GroupAlarms);

        // A second schedule is applied behind the page's back, then the service announces the cycle.
        var second = await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA), Item(ScheduleB, time: "09:00") }, revision: 2));
        Assert.True(second.Succeeded, string.Join(",", second.Failures.Select(f => f.Code + ":" + f.Message)));
        Assert.Equal(3, (await _store.GetAllAsync()).Count);   // manual + two cloud alarms

        // The notification itself must not schedule or cancel anything: only the projection is refreshed.
        var schedulesBefore = _scheduler.ScheduledAlarms.Count;
        var cancelsBefore = _scheduler.CancelledAlarmIds.Count;
        _cloud.RaiseSyncCompleted();
        await vm.WaitForPendingProjectionAsync();

        Assert.Equal(2, vm.GroupAlarms.Count);
        Assert.Equal(schedulesBefore, _scheduler.ScheduledAlarms.Count);
        Assert.Equal(cancelsBefore, _scheduler.CancelledAlarmIds.Count);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task NotificationDuringBusy_DoesNotProject_AndOneCatchUpHappensAfterwards()
    {
        await SaveRegistrationAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var counting = new T118CountingGroupAlarmSource(_groupSource);
        var vm = NewViewModel(counting);

        vm.IsBusy = true;   // simulate an in-flight load owned by another code path
        _cloud.RaiseSyncCompleted();
        _cloud.RaiseSyncCompleted();
        _cloud.RaiseSyncCompleted();
        await vm.WaitForPendingProjectionAsync();

        Assert.Equal(0, counting.LoadCount);   // deferred, not three separate reads

        vm.IsBusy = false;
        await vm.ReadOnlyRefreshProjectionAsync();

        Assert.Equal(1, counting.LoadCount);
        Assert.Single(vm.GroupAlarms);
    }

    // --- 5) unchanged projection does not replace rows ---------------------

    [Fact]
    public async Task UnchangedProjection_DoesNotReplaceAnyRow()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        var manualRow = vm.Alarms[0];
        var groupRow = vm.GroupAlarms[0];
        var events = 0;
        var replaces = 0;

        NotifyCollectionChangedEventHandler counter = (_, args) =>
        {
            events++;
            if (args.Action == NotifyCollectionChangedAction.Replace) replaces++;
        };

        vm.Alarms.CollectionChanged += counter;
        vm.GroupAlarms.CollectionChanged += counter;
        try
        {
            await vm.ReadOnlyRefreshProjectionAsync();
            await vm.ReadOnlyRefreshProjectionAsync();
        }
        finally
        {
            vm.Alarms.CollectionChanged -= counter;
            vm.GroupAlarms.CollectionChanged -= counter;
        }

        Assert.Equal(0, events);
        Assert.Equal(0, replaces);
        Assert.Same(manualRow, vm.Alarms[0]);
        Assert.Same(groupRow, vm.GroupAlarms[0]);
    }

    [Fact]
    public async Task ChangedGroupRow_IsReplacedInPlace()
    {
        await SaveRegistrationAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();
        var before = vm.GroupAlarms[0];

        var stored = await _store.GetByIdAsync(before.LocalAlarmId!);
        Assert.NotNull(stored);
        stored!.IsEnabled = false;
        stored.NextFireTime = null;
        await _store.SaveAsync(stored);

        await vm.ReadOnlyRefreshProjectionAsync();

        var after = Assert.Single(vm.GroupAlarms);
        Assert.NotSame(before, after);
        Assert.Equal(GroupAlarmProjector.EnabledTextOff, after.EnabledText);
        Assert.Null(after.NextFireText);
    }

    // --- 6) the group navigation ------------------------------------------

    [Fact]
    public async Task OpenGroupCommand_UsesTheExistingCloudRouteWithAnEscapedGroupId()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA, groupId: "group/one two") }));

        // T187R: the id handed to the editor is the live server owner of this schedule, not a copy of
        // the row's manifest field. Here they agree, so the historical route string is unchanged.
        _cloud.Groups.Add(new AlarmGroupDto(
            "group/one two", "グループ朝", true, 3, null, "weekly", new[] { 1, 2, 3, 4, 5 }, null,
            "Asia/Tokyo", true, Array.Empty<string>(),
            new[] { new AlarmTargetDto(ScheduleA, DeviceId, "07:00", "default", 80, true, true) }));

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();
        var row = Assert.Single(vm.GroupAlarms);
        Assert.Equal("group/one two", row.GroupId);

        await vm.OpenGroupCommand.ExecuteAsync(row);

        var route = Assert.Single(_navigatedRoutes);
        Assert.Equal(
            $"CloudAlarmGroupEditPage?groupId={Uri.EscapeDataString("group/one two")}",
            route);
        Assert.Equal(1, _cloud.ListGroupsCallCount);
    }

    [Fact]
    public async Task OpenGroupCommand_IsNotExecutedForARowWithoutAGroupLink()
    {
        await SaveRegistrationAsync();
        var oldBinding = new CloudSyncBinding("https://api.wakemesh.local", "owner-account", DeviceId, 9);
        await _store.SaveAsync(new LocalAlarmModel
        {
            Id = SyncLocalAlarmIdentity.GetLocalAlarmId(oldBinding, ScheduleA),
            Name = "旧クラウド",
            Time = new TimeOnly(7, 0),
            DeviceId = "t118-device"
        });

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();
        var row = Assert.Single(vm.GroupAlarms);
        Assert.Equal(GroupAlarmOrigin.CloudOrphan, row.Origin);
        Assert.False(vm.OpenGroupCommand.CanExecute(row));

        await vm.OpenGroupCommand.ExecuteAsync(row);

        Assert.Empty(_navigatedRoutes);
    }

    [Fact]
    public async Task SignedOutCloud_DoesNotAllowGroupEditing()
    {
        await SaveRegistrationAsync();
        await _applier.ApplyAsync(Snapshot(new[] { Item(ScheduleA) }));
        _cloud.IsConfiguredAndSignedIn = false;

        var vm = NewViewModel();
        await vm.LoadAlarmsAsync();

        Assert.False(vm.CanEditCloudGroups);
        var row = Assert.Single(vm.GroupAlarms);
        Assert.False(vm.OpenGroupCommand.CanExecute(row));
        await vm.OpenGroupCommand.ExecuteAsync(row);
        Assert.Empty(_navigatedRoutes);
    }

    // --- 7) the manual editor refuses cloud alarms -------------------------

    [Fact]
    public async Task AlarmEdit_RefusesCloudPrefixedAlarms_OnLoadAndSave()
    {
        await SaveRegistrationAsync();
        var vm = NewEditViewModel();

        var cloudAlarm = new LocalAlarmModel
        {
            Id = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, ScheduleA),
            Name = "グループ朝"
        };

        vm.LoadFrom(cloudAlarm);
        Assert.Equal(AlarmEditViewModel.NotEditableMessage, vm.ErrorMessage);
        Assert.True(vm.IsEditRefused);

        vm.Name = "書き換え";
        Assert.False(await vm.SaveAlarmAsync());
        Assert.Equal(AlarmEditViewModel.NotEditableMessage, vm.ErrorMessage);
        Assert.Null(await _store.GetByIdAsync(cloudAlarm.Id));
    }

    [Fact]
    public async Task AlarmEdit_RefusesLegacyManifestManagedIds_OnSave()
    {
        await SaveRegistrationAsync();
        await SaveManualAlarmAsync("legacy-cloud-alarm", "旧グループ");
        var manifest = await _manifestStore.LoadAsync();
        manifest.Schedules[ScheduleA] = new SyncScheduleEntry(ScheduleA, "legacy-cloud-alarm", "group-legacy", 5);
        await _manifestStore.SaveAsync(manifest);

        var vm = NewEditViewModel();
        vm.Id = "legacy-cloud-alarm";
        vm.Name = "書き換え";

        Assert.False(await vm.SaveAlarmAsync());
        Assert.Equal(AlarmEditViewModel.NotEditableMessage, vm.ErrorMessage);

        var stored = await _store.GetByIdAsync("legacy-cloud-alarm");
        Assert.NotNull(stored);
        Assert.Equal("旧グループ", stored!.Name);
    }

    [Fact]
    public async Task AlarmEdit_StillSavesAnOrdinaryManualAlarm()
    {
        await SaveRegistrationAsync();
        var vm = NewEditViewModel();
        vm.Id = "manual-edit-t118";
        vm.Name = "普通のアラーム";

        Assert.True(await vm.SaveAlarmAsync());
        Assert.NotNull(await _store.GetByIdAsync("manual-edit-t118"));
    }

    private AlarmEditViewModel NewEditViewModel() => new(
        _coordinator,
        new LocalDeviceInfoProvider("t118-device"),
        audioPlaybackService: null,
        groupSource: _groupSource);

    // --- 8) DI seam ---------------------------------------------------------

    [Fact]
    public void DependencyInjection_ResolvesTheViewModelsWithTheNewOptionalDependencies()
    {
        var services = new ServiceCollection();
        var alarmsPath = Path.Combine(_tempDir, "di_alarms.json");
        var deviceIdPath = Path.Combine(_tempDir, "di_device_id.txt");
        var settingsPath = Path.Combine(_tempDir, "di_auth_settings.json");

        services.AddWakeMeshCoreServices(alarmsPath, deviceIdPath);
        services.AddWakeMeshAuthServices(settingsPath);
        services.AddSingleton<IAlarmScheduler, StubAlarmScheduler>();
        services.AddSingleton<IPermissionService, StubPermissionService>();
        services.AddSingleton<IAlarmRingingService, StubAlarmRingingService>();
        services.AddSingleton<IAudioPlaybackService, StubAudioPlaybackService>();
        services.AddSingleton<ISecureStorageProvider, TestSecureStorageProvider>();
        services.AddSingleton<IMsalClientAdapter, TestMsalClientAdapter>();

        // The same registrations MauiProgram adds for T118.
        services.AddSingleton<ICloudDeviceRegistrationStore>(
            _ => new FileCloudDeviceRegistrationStore(Path.Combine(_tempDir, "di_registration.json")));
        services.AddSingleton<ICloudManagementService, CloudManagementService>();
        services.AddSingleton<ICloudNavigation, MauiShellCloudNavigation>();
        services.AddSingleton<DesiredObservationStore>();
        services.AddSingleton<IGroupAlarmSource>(sp => new GroupAlarmSource(
            sp.GetRequiredService<ICloudDeviceRegistrationStore>(), _tempDir));

        services.AddTransient<AlarmListViewModel>();
        services.AddTransient<AlarmEditViewModel>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var list = provider.GetRequiredService<AlarmListViewModel>();
        var edit = provider.GetRequiredService<AlarmEditViewModel>();

        Assert.NotNull(list);
        Assert.NotNull(edit);
        Assert.IsType<GroupAlarmSource>(provider.GetRequiredService<IGroupAlarmSource>());
        Assert.NotNull(provider.GetRequiredService<DesiredObservationStore>());
        Assert.False(list.CanEditCloudGroups);   // not signed in: nothing to open
    }

    [Fact]
    public void MauiProgram_RegistersTheObservationStoreAndTheGroupSource()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "WakeMesh.Mobile", "MauiProgram.cs"));

        Assert.Contains("AddSingleton<DesiredObservationStore>()", source);
        Assert.Contains("AddSingleton<IGroupAlarmSource, GroupAlarmSource>()", source);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "WakeMesh.Mobile")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
