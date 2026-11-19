using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

/// <summary>
/// T22 end-to-end: UI day chips (0=Sun..6=Sat) → real API → desired → MobileDesiredApplier →
/// Mobile next-fire, including Sunday, holiday skip, week crossover, and invalid day 7 rejection.
/// </summary>
public sealed class T22WeekdayContractRoundTripTests : IClassFixture<CloudUiTestApiFactory>, IDisposable
{
    private readonly CloudUiTestApiFactory _factory;
    private readonly string _root;

    public T22WeekdayContractRoundTripTests(CloudUiTestApiFactory factory)
    {
        _factory = factory;
        _root = Path.Combine(Path.GetTempPath(), "wakemesh-t22-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    public static TheoryData<Action<CloudAlarmGroupEditViewModel>, int, Weekdays, DayOfWeek> UiDayCases => new()
    {
        { vm => { ClearDays(vm); vm.DaySunday = true; }, 0, Weekdays.Sunday, DayOfWeek.Sunday },
        { vm => { ClearDays(vm); vm.DayMonday = true; }, 1, Weekdays.Monday, DayOfWeek.Monday },
        { vm => { ClearDays(vm); vm.DayTuesday = true; }, 2, Weekdays.Tuesday, DayOfWeek.Tuesday },
        { vm => { ClearDays(vm); vm.DayWednesday = true; }, 3, Weekdays.Wednesday, DayOfWeek.Wednesday },
        { vm => { ClearDays(vm); vm.DayThursday = true; }, 4, Weekdays.Thursday, DayOfWeek.Thursday },
        { vm => { ClearDays(vm); vm.DayFriday = true; }, 5, Weekdays.Friday, DayOfWeek.Friday },
        { vm => { ClearDays(vm); vm.DaySaturday = true; }, 6, Weekdays.Saturday, DayOfWeek.Saturday },
    };

    [Theory]
    [MemberData(nameof(UiDayCases))]
    public async Task UiChip_Save_Desired_Apply_NextFire_MatchesWireDay(
        Action<CloudAlarmGroupEditViewModel> selectDay,
        int expectedWireDay,
        Weekdays expectedBit,
        DayOfWeek expectedDow)
    {
        var ctx = await CreateContextAsync("day-" + expectedWireDay);
        selectDay(ctx.Edit);
        ctx.Edit.Label = "T22-day-" + expectedWireDay;
        ctx.Edit.ExcludeHolidays = false;
        ctx.Edit.Targets.Clear();
        ctx.Edit.Targets.Add(new CloudAlarmTargetItemViewModel(
            ctx.DeviceId,
            "dev",
            new TimeSpan(9, 0, 0),
            "default",
            80,
            true,
            true));

        await ctx.Edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(ctx.Edit.ErrorMessage);

        var listed = await ctx.Client.ListAlarmGroupsAsync();
        Assert.True(listed.IsSuccess);
        var group = Assert.Single(listed.Value!, g => g.Id == ctx.Edit.GroupId);
        Assert.Equal(new[] { expectedWireDay }, group.Days!.OrderBy(d => d).ToArray());

        var reload = new CloudAlarmGroupEditViewModel(ctx.Service);
        await reload.InitializeAsync(ctx.Edit.GroupId);
        Assert.Equal(expectedWireDay == 0, reload.DaySunday);
        Assert.Equal(expectedWireDay == 1, reload.DayMonday);
        Assert.Equal(expectedWireDay == 2, reload.DayTuesday);
        Assert.Equal(expectedWireDay == 3, reload.DayWednesday);
        Assert.Equal(expectedWireDay == 4, reload.DayThursday);
        Assert.Equal(expectedWireDay == 5, reload.DayFriday);
        Assert.Equal(expectedWireDay == 6, reload.DaySaturday);

        var desired = await ctx.Client.GetDesiredAsync(ctx.DeviceId);
        Assert.True(desired.IsSuccess);
        var item = Assert.Single(desired.Value!.Items, i => i.GroupId == ctx.Edit.GroupId);
        Assert.Equal(new[] { expectedWireDay }, item.Days!.OrderBy(d => d).ToArray());

        var sync = await ctx.Service.SyncCurrentDeviceAsync();
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, sync.Status);

        var alarm = Assert.Single(await ctx.Coordinator.GetAllAsync());
        Assert.Equal(expectedBit, alarm.ActiveWeekdays);

        var after = new DateTimeOffset(2026, 3, 14, 10, 0, 0, TimeSpan.FromHours(9)); // Saturday
        var next = ctx.Calculator.CalculateNextFire(alarm, after);
        Assert.NotNull(next);
        Assert.Equal(expectedDow, next!.LocalDate.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 0), next.LocalTime);

        Assert.True(ctx.Scheduler.ScheduledAlarms.ContainsKey(alarm.Id));
    }

    [Fact]
    public async Task SundayOnly_UiRoundTrip_ComputesActualNextSunday()
    {
        var ctx = await CreateContextAsync("sunday-next");
        ClearDays(ctx.Edit);
        ctx.Edit.DaySunday = true;
        ctx.Edit.Label = "Sunday-only";
        ctx.Edit.ExcludeHolidays = false;
        ctx.Edit.Targets.Clear();
        ctx.Edit.Targets.Add(new CloudAlarmTargetItemViewModel(
            ctx.DeviceId, "dev", new TimeSpan(7, 15, 0)));

        await ctx.Edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(ctx.Edit.ErrorMessage);

        var desired = await ctx.Client.GetDesiredAsync(ctx.DeviceId);
        Assert.Contains(0, desired.Value!.Items.Single().Days!);

        Assert.Equal(
            DesiredSyncStatus.AppliedAcknowledged,
            (await ctx.Service.SyncCurrentDeviceAsync()).Status);

        var alarm = Assert.Single(await ctx.Coordinator.GetAllAsync());
        Assert.Equal(Weekdays.Sunday, alarm.ActiveWeekdays);

        var afterSat = new DateTimeOffset(2026, 6, 13, 8, 0, 0, TimeSpan.FromHours(9));
        var next = ctx.Calculator.CalculateNextFire(alarm, afterSat);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 6, 14), next!.LocalDate);
        Assert.Equal(DayOfWeek.Sunday, next.LocalDate.DayOfWeek);
    }

    [Fact]
    public async Task SundayHolidayExclude_RoundTrip_SkipsHolidaySunday()
    {
        var ctx = await CreateContextAsync("sunday-holiday");
        ctx.Holidays.Holidays = new HashSet<DateOnly> { new(2026, 6, 14) };

        ClearDays(ctx.Edit);
        ctx.Edit.DaySunday = true;
        ctx.Edit.Label = "Sunday-holiday";
        ctx.Edit.ExcludeHolidays = true;
        ctx.Edit.Targets.Clear();
        ctx.Edit.Targets.Add(new CloudAlarmTargetItemViewModel(
            ctx.DeviceId, "dev", new TimeSpan(7, 0, 0)));

        await ctx.Edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(ctx.Edit.ErrorMessage);
        Assert.Equal(
            DesiredSyncStatus.AppliedAcknowledged,
            (await ctx.Service.SyncCurrentDeviceAsync()).Status);

        var alarm = Assert.Single(await ctx.Coordinator.GetAllAsync());
        Assert.True(alarm.ExcludeHolidays);
        Assert.Equal(Weekdays.Sunday, alarm.ActiveWeekdays);

        var next = ctx.Calculator.CalculateNextFire(
            alarm,
            new DateTimeOffset(2026, 6, 13, 8, 0, 0, TimeSpan.FromHours(9)));
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 6, 21), next!.LocalDate);
    }

    [Fact]
    public async Task WeekCrossover_SaturdayChip_FromFridayNight_LandsSaturday()
    {
        var ctx = await CreateContextAsync("week-cross");
        ClearDays(ctx.Edit);
        ctx.Edit.DaySaturday = true;
        ctx.Edit.Label = "Sat-cross";
        ctx.Edit.ExcludeHolidays = false;
        ctx.Edit.Targets.Clear();
        ctx.Edit.Targets.Add(new CloudAlarmTargetItemViewModel(
            ctx.DeviceId, "dev", new TimeSpan(6, 0, 0)));

        await ctx.Edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(ctx.Edit.ErrorMessage);
        Assert.Equal(
            DesiredSyncStatus.AppliedAcknowledged,
            (await ctx.Service.SyncCurrentDeviceAsync()).Status);

        var alarm = Assert.Single(await ctx.Coordinator.GetAllAsync());
        Assert.Equal(Weekdays.Saturday, alarm.ActiveWeekdays);

        var next = ctx.Calculator.CalculateNextFire(
            alarm,
            new DateTimeOffset(2026, 3, 13, 22, 0, 0, TimeSpan.FromHours(9))); // Friday night
        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Saturday, next!.LocalDate.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 3, 14), next.LocalDate);
    }

    [Fact]
    public async Task WireDay7_ApiRejects400_NoGroupCreated_AndApplierInvalidItem_NoSideEffects()
    {
        var ctx = await CreateContextAsync("day7-reject");

        var badCreate = await ctx.Client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            Id: "bad-day-7",
            Label: "legacy-seven",
            Enabled: true,
            Repeat: "weekly",
            Days: new[] { 7 },
            Date: null,
            TimeZone: "Asia/Tokyo",
            ExcludeHolidays: false,
            ExcludedDates: Array.Empty<string>(),
            Targets: new[]
            {
                new AlarmTargetDto(null, ctx.DeviceId, "07:00", "default", 80, true, true)
            }));
        Assert.False(badCreate.IsSuccess);
        Assert.Equal(400, badCreate.StatusCode);
        Assert.Contains("invalidDays", badCreate.ErrorCode ?? badCreate.Message ?? "", StringComparison.OrdinalIgnoreCase);

        var listed = await ctx.Client.ListAlarmGroupsAsync();
        Assert.True(listed.IsSuccess);
        Assert.DoesNotContain(listed.Value!, g => g.Id == "bad-day-7");

        ClearDays(ctx.Edit);
        ctx.Edit.DayMonday = true;
        ctx.Edit.Label = "seed-mon";
        ctx.Edit.Targets.Clear();
        ctx.Edit.Targets.Add(new CloudAlarmTargetItemViewModel(
            ctx.DeviceId, "dev", new TimeSpan(6, 0, 0)));
        await ctx.Edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(ctx.Edit.ErrorMessage);
        Assert.Equal(
            DesiredSyncStatus.AppliedAcknowledged,
            (await ctx.Service.SyncCurrentDeviceAsync()).Status);

        var before = (await ctx.Coordinator.GetAllAsync())
            .Select(a => (a.Id, a.Name, a.ActiveWeekdays, a.Time))
            .OrderBy(x => x.Id)
            .ToArray();
        var scheduleBefore = ctx.Scheduler.ScheduledAlarms.Count;

        var binding = new CloudSyncBinding(
            "https://api.wakemesh.local",
            "test-owner-account-id",
            ctx.DeviceId,
            1);
        var manifestPath = Path.Combine(ctx.Dir, $"sync_manifest_{binding.ToStorageKey()}.json");
        var applier = new MobileDesiredApplier(
            binding,
            ctx.Coordinator,
            new FileCloudSyncManifestStore(binding, manifestPath));

        var badSnap = new DesiredSnapshotDto(
            ctx.DeviceId,
            99,
            Guid.NewGuid(),
            new[]
            {
                new DesiredItemDto(
                    Guid.NewGuid(),
                    "g-bad",
                    "bad7",
                    true,
                    "weekly",
                    new[] { 7 },
                    null,
                    "Asia/Tokyo",
                    false,
                    Array.Empty<string>(),
                    "08:00",
                    "c",
                    50,
                    true,
                    true,
                    99)
            },
            Array.Empty<DesiredTombstoneDto>());

        var apply = await applier.ApplyAsync(badSnap);
        Assert.False(apply.Succeeded);
        Assert.Contains(apply.Failures, f => f.Code == "InvalidItem");

        var after = (await ctx.Coordinator.GetAllAsync())
            .Select(a => (a.Id, a.Name, a.ActiveWeekdays, a.Time))
            .OrderBy(x => x.Id)
            .ToArray();
        Assert.Equal(before, after);
        Assert.Equal(scheduleBefore, ctx.Scheduler.ScheduledAlarms.Count);
    }

    [Fact]
    public async Task ManualLocalAlarm_Preserved_AndCoreBitsStable_AcrossSundayCloudApply()
    {
        var ctx = await CreateContextAsync("manual-preserve");
        var manual = new LocalAlarmModel
        {
            Id = "manual-t22",
            Name = "手動ローカル",
            Time = new TimeOnly(5, 30),
            IsEnabled = true,
            DeviceId = "local",
            ActiveWeekdays = Weekdays.Tuesday | Weekdays.Thursday,
            RepeatKind = AlarmRepeatKind.Weekly,
            TimeZoneId = "Asia/Tokyo",
        };
        await ctx.Coordinator.SaveAndScheduleAsync(manual);

        ClearDays(ctx.Edit);
        ctx.Edit.DaySunday = true;
        ctx.Edit.Label = "クラウド日曜";
        ctx.Edit.Targets.Clear();
        ctx.Edit.Targets.Add(new CloudAlarmTargetItemViewModel(
            ctx.DeviceId, "dev", new TimeSpan(9, 0, 0)));
        await ctx.Edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(ctx.Edit.ErrorMessage);
        Assert.Equal(
            DesiredSyncStatus.AppliedAcknowledged,
            (await ctx.Service.SyncCurrentDeviceAsync()).Status);

        var all = await ctx.Coordinator.GetAllAsync();
        Assert.Equal(2, all.Count);
        var still = Assert.Single(all, a => a.Id == "manual-t22");
        Assert.Equal(Weekdays.Tuesday | Weekdays.Thursday, still.ActiveWeekdays);
        Assert.Equal(1 << 0, (int)Weekdays.Monday);
        Assert.Equal(1 << 6, (int)Weekdays.Sunday);
        Assert.Equal(Weekdays.Sunday, Assert.Single(all, a => a.Id != "manual-t22").ActiveWeekdays);
    }

    private static void ClearDays(CloudAlarmGroupEditViewModel vm)
    {
        vm.DayMonday = false;
        vm.DayTuesday = false;
        vm.DayWednesday = false;
        vm.DayThursday = false;
        vm.DayFriday = false;
        vm.DaySaturday = false;
        vm.DaySunday = false;
    }

    private async Task<Ctx> CreateContextAsync(string leaf)
    {
        var dir = Path.Combine(_root, leaf);
        Directory.CreateDirectory(dir);

        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var auth = new MockAuthService("https://api.wakemesh.local", token);
        var creds = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => auth.CurrentBinding);
        var reg = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        var holidays = new FakeHolidayService();
        var calculator = new AlarmScheduleCalculator(holidays);
        var scheduler = new FakeAlarmScheduler();
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(dir, "alarms.json")),
            calculator,
            scheduler);

        WakeMeshApiClient ClientFactory() => TestClientHelper.CreateApiClient(http, auth, creds);
        var service = new CloudManagementService(auth, reg, creds, coordinator, ClientFactory, customStorageDir: dir);

        var regResult = await service.RegisterCurrentDeviceAsync("t22-" + leaf);
        Assert.True(regResult.IsSuccess, regResult.Message);
        var deviceId = regResult.Value!.Id;

        var edit = new CloudAlarmGroupEditViewModel(service);
        await edit.InitializeAsync();

        return new Ctx(dir, deviceId, ClientFactory(), service, coordinator, calculator, scheduler, holidays, edit);
    }

    private sealed record Ctx(
        string Dir,
        Guid DeviceId,
        WakeMeshApiClient Client,
        CloudManagementService Service,
        AlarmCoordinator Coordinator,
        AlarmScheduleCalculator Calculator,
        FakeAlarmScheduler Scheduler,
        FakeHolidayService Holidays,
        CloudAlarmGroupEditViewModel Edit);
}
