using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;

namespace WakeMesh.Mobile.Services.CloudManagement;

public interface ICloudManagementService
{
    bool IsConfiguredAndSignedIn { get; }
    string? CurrentUserAccountId { get; }

    Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(
        string displayName,
        CancellationToken cancellationToken = default);

    Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<ApiResult> RetireDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task<ApiResult> RevokeDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(
        CancellationToken cancellationToken = default);

    Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
        AlarmGroupWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
        string groupId,
        AlarmGroupWriteRequest request,
        long ifMatchVersion,
        CancellationToken cancellationToken = default);

    Task<ApiResult> DeleteAlarmGroupAsync(
        string groupId,
        long ifMatchVersion,
        CancellationToken cancellationToken = default);

    Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(
        CancellationToken cancellationToken = default);

    event EventHandler? RegistrationChanged;
    event EventHandler? SyncCompleted;
}
