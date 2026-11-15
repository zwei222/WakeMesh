// T115: dedicated test double for CloudAlarmGroupEditViewModel threading probes.
// The real ICloudManagementService awaits with ConfigureAwait(false), so every continuation in the
// view model lands on a thread-pool thread. This fake reproduces that shape (a real incomplete task
// is awaited, no synchronous completion) so a probe can detect UI-bound state being applied off the
// UI thread. No HTTP, no credentials, no production file is affected.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Services.CloudManagement;

namespace WakeMesh.Mobile.CloudUi.Tests.TestDoubles;

internal sealed class T115FakeCloudManagementService : ICloudManagementService
{
    /// <summary>Yields like a real network call: the awaiting continuation must not stay on the caller thread.</summary>
    private static async Task<T> HopsAsync<T>(T value)
    {
        await Task.Delay(1).ConfigureAwait(false);
        return value;
    }

    public bool IsConfiguredAndSignedIn { get; set; } = true;
    public string? CurrentUserAccountId { get; set; } = "t115-account";

    public int ListDevicesCallCount { get; private set; }
    public int ListGroupsCallCount { get; private set; }

    public AlarmGroupWriteRequest? LastCreateRequest { get; private set; }
    public AlarmGroupWriteRequest? LastReplaceRequest { get; private set; }
    public long LastIfMatchVersion { get; private set; }

    public Func<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesResult { get; set; } =
        () => ApiResult<IReadOnlyList<DeviceListItem>>.Ok(Array.Empty<DeviceListItem>());

    public Func<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListGroupsResult { get; set; } =
        () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(Array.Empty<AlarmGroupDto>());

    public CloudDeviceRegistration? Registration { get; set; }

    public Exception? ListDevicesException { get; set; }
    public Exception? CreateGroupException { get; set; }

    public GroupSaveOutcome CreateGroupOutcome { get; set; } = T115Fixtures.Group("group-created", 3);

    public GroupSaveOutcome ReplaceGroupOutcome { get; set; } = T115Fixtures.Group("group-replaced", 4);

    public async Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        ListDevicesCallCount++;
        if (ListDevicesException != null)
        {
            await Task.Delay(1).ConfigureAwait(false);
            throw ListDevicesException;
        }

        return await HopsAsync(ListDevicesResult()).ConfigureAwait(false);
    }

    public async Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default)
        => await HopsAsync(Registration).ConfigureAwait(false);

    public async Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(CancellationToken cancellationToken = default)
    {
        ListGroupsCallCount++;
        return await HopsAsync(ListGroupsResult()).ConfigureAwait(false);
    }

    public async Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
        AlarmGroupWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        LastCreateRequest = request;
        if (CreateGroupException != null)
        {
            await Task.Delay(1).ConfigureAwait(false);
            throw CreateGroupException;
        }

        return await HopsAsync(ApiResult<GroupSaveOutcome>.Ok(CreateGroupOutcome)).ConfigureAwait(false);
    }

    public async Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
        string groupId,
        AlarmGroupWriteRequest request,
        long ifMatchVersion,
        CancellationToken cancellationToken = default)
    {
        LastReplaceRequest = request;
        LastIfMatchVersion = ifMatchVersion;
        return await HopsAsync(ApiResult<GroupSaveOutcome>.Ok(ReplaceGroupOutcome)).ConfigureAwait(false);
    }

    public Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(
        string displayName,
        CancellationToken cancellationToken = default)
        => HopsAsync(ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Unexpected));

    public Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => HopsAsync(ApiResult.Ok());

    public Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => HopsAsync(ApiResult.Ok());

    public Task<ApiResult> DeleteAlarmGroupAsync(
        string groupId,
        long ifMatchVersion,
        CancellationToken cancellationToken = default)
        => HopsAsync(ApiResult.Ok());

    public Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("T115 does not exercise device sync.");

    public event EventHandler? RegistrationChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? SyncCompleted
    {
        add { }
        remove { }
    }
}

internal static class T115Fixtures
{
    public static DeviceListItem Device(Guid id, string name) =>
        new(id, name, "Active", 0, 0, "active", false, null, null);

    public static CloudDeviceRegistration Registration(Guid id, string name) => new(id, 1, "https://api.wakemesh.local", "acct", name);

    public static AlarmGroupDto GroupDto(string id, long version) =>
        new(id, "グループ", true, version, null, "weekly", new[] { 1, 2 }, null, "Asia/Tokyo", true,
            Array.Empty<string>(), Array.Empty<AlarmTargetDto>());

    public static GroupSaveOutcome Group(string id, long version) =>
        new(GroupDto(id, version), new NotificationStatusDto("queued", null));
}
