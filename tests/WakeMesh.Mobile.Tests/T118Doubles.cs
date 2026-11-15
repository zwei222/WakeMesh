using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T118 doubles for the alarm-list projection tests. They implement the real contracts (no ad-hoc shapes)
/// so the compiler keeps this honest if the interfaces change.
/// </summary>
public sealed class T118CloudStub : ICloudManagementService
{
    public bool IsConfiguredAndSignedIn { get; set; } = true;
    public string? CurrentUserAccountId { get; set; } = "owner-account";

    public event EventHandler? RegistrationChanged;
    public event EventHandler? SyncCompleted;

    public void RaiseRegistrationChanged() => RegistrationChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseSyncCompleted() => SyncCompleted?.Invoke(this, EventArgs.Empty);

    public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CloudDeviceRegistration?>(null);

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

    public Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, null, "stub"));
}

/// <summary>Counts how often the projection really read the read-only source.</summary>
public sealed class T118CountingGroupAlarmSource : IGroupAlarmSource
{
    private readonly IGroupAlarmSource _inner;
    private int _loads;

    public T118CountingGroupAlarmSource(IGroupAlarmSource inner) => _inner = inner;

    public int LoadCount => Volatile.Read(ref _loads);

    public Task<GroupAlarmSourceResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loads);
        return _inner.LoadAsync(cancellationToken);
    }
}
