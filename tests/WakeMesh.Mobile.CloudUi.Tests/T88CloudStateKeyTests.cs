using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T88 / T80 P0-9 + P1-1 + P1-4.
/// The cloud tab now picks its colour and mark from a state key instead of a hard-coded colour, so
/// the key must follow the same facts as the text: a 0-revision device with a recorded successful
/// sync is "ok", an unconfirmed retirement is "warn", a finished retirement is "inactive", and a
/// group's targets must report revokedUnconfirmed / retiring devices instead of falling through to
/// "反映待ち" (which would claim a reflection is still coming when it never will be).
/// </summary>
public sealed class T88CloudStateKeyTests
{
    private static DeviceListItem Row(
        long desired,
        long applied,
        bool? hasSuccessfulSync,
        string retirementState = "active",
        string status = "Active",
        bool cancelConfirmed = false) =>
        new(Guid.NewGuid(), "Fold5", status, desired, applied, retirementState, cancelConfirmed, null, hasSuccessfulSync);

    // --- device row state key -----------------------------------------

    [Fact]
    public void ZeroRevision_AcknowledgedEmptyConfiguration_IsOk()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, hasSuccessfulSync: true));

        Assert.Equal("ok", item.StateKey);
        Assert.Contains("同期済み", item.SyncStatusText);
    }

    [Fact]
    public void ZeroRevision_NoRecordedAck_IsPending()
    {
        Assert.Equal("pending", new CloudDeviceItemViewModel(Row(0, 0, hasSuccessfulSync: false)).StateKey);
    }

    [Fact]
    public void ZeroRevision_OldServerWithoutProjection_IsUnknown()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, hasSuccessfulSync: null));

        Assert.Equal("unknown", item.StateKey);
        Assert.DoesNotContain("同期済み", item.SyncStatusText);
    }

    [Fact]
    public void AppliedRevision_IsOk_AndUnappliedIsPending()
    {
        Assert.Equal("ok", new CloudDeviceItemViewModel(Row(5, 5, hasSuccessfulSync: true)).StateKey);
        Assert.Equal("pending", new CloudDeviceItemViewModel(Row(5, 2, hasSuccessfulSync: true)).StateKey);
    }

    [Fact]
    public void FinishedRetirement_IsInactive_RatherThanAWarning()
    {
        var retired = new CloudDeviceItemViewModel(Row(0, 0, true, "retired", "Retiring", cancelConfirmed: true));
        var revoked = new CloudDeviceItemViewModel(Row(0, 0, true, "revoked", "Revoked", cancelConfirmed: true));

        Assert.Equal("inactive", retired.StateKey);
        Assert.Equal("inactive", revoked.StateKey);
        Assert.Contains("登録解除", retired.SyncStatusText);
        Assert.DoesNotContain("退役", retired.SyncStatusText);
        Assert.DoesNotContain("即時取消", revoked.SyncStatusText);
    }

    [Fact]
    public void UnconfirmedRevocation_IsWarn_AndNeverClaimsTheDeviceAlarmsAreGone()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, true, "revokedUnconfirmed", "Revoked"));

        Assert.Equal("warn", item.StateKey);
        Assert.Contains("未確認", item.SyncStatusText);
        Assert.DoesNotContain("完了", item.SyncStatusText);
    }

    [Fact]
    public void UnconfirmedRetirement_IsWarn()
    {
        var item = new CloudDeviceItemViewModel(Row(0, 0, true, "retiring", "Retiring"));

        Assert.Equal("warn", item.StateKey);
        Assert.Contains("登録解除待ち", item.SyncStatusText);
    }

    [Fact]
    public void StateKey_IsReadOnly_AndExistingColourContractStillResolves()
    {
        var property = typeof(CloudDeviceItemViewModel).GetProperty(nameof(CloudDeviceItemViewModel.StateKey));

        Assert.NotNull(property);
        Assert.Null(property!.GetSetMethod(nonPublic: true));

        var ok = new CloudDeviceItemViewModel(Row(5, 5, true));
        Assert.Equal("#10B981", ok.SyncStatusColorHex);
    }

    // --- group / target reflection state ------------------------------

    private static AlarmGroupDto Group(params AlarmTargetDto[] targets) =>
        new("g1", "平日の起床", true, 4, null, "weekly", new[] { 1, 2, 3, 4, 5 }, null, "Asia/Tokyo", true,
            Array.Empty<string>(), targets);

    private static AlarmTargetDto Target(Guid deviceId) =>
        new(null, deviceId, "06:30", "default", 80, true, true);

    [Fact]
    public void Targets_UnconfirmedRevocation_IsWarn_NotPendingReflection()
    {
        var deviceId = Guid.NewGuid();
        var statuses = new Dictionary<Guid, DeviceListItem>
        {
            [deviceId] = Row(2, 2, true, "revokedUnconfirmed", "Revoked")
        };

        var group = new CloudAlarmGroupItemViewModel(Group(Target(deviceId)), null, statuses);
        var target = Assert.Single(group.Targets);

        Assert.Equal("warn", target.ReflectionStateKey);
        Assert.Contains("未確認", target.ReflectionStatusText);
        Assert.DoesNotContain("反映待ち", target.ReflectionStatusText);
    }

    [Fact]
    public void Targets_RetiringDevice_IsPending_NotAMissingReflection()
    {
        var deviceId = Guid.NewGuid();
        var statuses = new Dictionary<Guid, DeviceListItem>
        {
            [deviceId] = Row(3, 1, true, "retiring", "Retiring")
        };

        var group = new CloudAlarmGroupItemViewModel(Group(Target(deviceId)), null, statuses);

        // Waiting on the device: never "applied", and still an attention state (not "inactive").
        Assert.Equal("warn", group.Targets[0].ReflectionStateKey);
        Assert.Contains("登録解除待ち", group.Targets[0].ReflectionStatusText);
        Assert.DoesNotContain("反映待ち", group.Targets[0].ReflectionStatusText);
        Assert.True(group.HasUnappliedTargets); // still waiting on the device: the warn tag stays
    }

    [Fact]
    public void Targets_RetiredDevice_IsInactive_AndDoesNotRaiseUnappliedTag()
    {
        var deviceId = Guid.NewGuid();
        var statuses = new Dictionary<Guid, DeviceListItem>
        {
            [deviceId] = Row(1, 1, true, "retired", "Retiring", cancelConfirmed: true)
        };

        var group = new CloudAlarmGroupItemViewModel(Group(Target(deviceId)), null, statuses);

        Assert.Equal("inactive", group.Targets[0].ReflectionStateKey);
        Assert.False(group.HasUnappliedTargets);
    }

    [Fact]
    public void HasUnappliedTargets_TrueWhenAnyTargetStillWaits()
    {
        var applied = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var statuses = new Dictionary<Guid, DeviceListItem>
        {
            [applied] = Row(4, 4, true),
            [waiting] = Row(4, 3, true)
        };

        var group = new CloudAlarmGroupItemViewModel(Group(Target(applied), Target(waiting)), null, statuses);

        Assert.Equal("ok", group.Targets[0].ReflectionStateKey);
        Assert.Equal("pending", group.Targets[1].ReflectionStateKey);
        Assert.True(group.HasUnappliedTargets);
    }

    [Fact]
    public void HasUnappliedTargets_FalseWhenEveryTargetIsApplied()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var statuses = new Dictionary<Guid, DeviceListItem>
        {
            [first] = Row(4, 4, true),
            [second] = Row(0, 0, hasSuccessfulSync: true)
        };

        var group = new CloudAlarmGroupItemViewModel(Group(Target(first), Target(second)), null, statuses);

        Assert.False(group.HasUnappliedTargets);
    }

    [Fact]
    public void GroupCopy_UsesPlainVersionWording()
    {
        var group = new CloudAlarmGroupItemViewModel(Group());

        Assert.Contains("サーバーに保存済み", group.ServerStatusText);
        Assert.Contains("バージョン4", group.ServerStatusText);
        Assert.DoesNotContain("v4", group.ServerStatusText);
        Assert.Contains("アラームを鳴らす端末", group.TargetsSummary);
    }

    // --- P1-1 / P1-4 cloud management copy ---------------------------

    [Fact]
    public void IsReauthRequired_FollowsRealAuthState_EvenWhenConfigured()
    {
        var auth = new MockAuthService("https://api.wakemesh.local", "token");
        var vm = new CloudManagementViewModel(new OfflineCloudManagementService(signedIn: true), auth);

        Assert.False(vm.IsReauthRequired);

        var changed = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        auth.SetState(AuthState.ReauthRequired);

        Assert.True(vm.IsReauthRequired);
        Assert.Contains(nameof(CloudManagementViewModel.IsReauthRequired), changed);
        Assert.Contains("サインインし直してください", vm.AuthStateText);
    }

    [Fact]
    public void FcmNoticeText_IsPlainJapaneseWithoutInternalNames()
    {
        var auth = new MockAuthService("https://api.wakemesh.local", "token");
        var vm = new CloudManagementViewModel(new OfflineCloudManagementService(signedIn: false), auth);

        Assert.DoesNotContain("FCM", vm.FcmNoticeText);
        Assert.DoesNotContain("※", vm.FcmNoticeText);
        Assert.Contains("未完了", vm.FcmNoticeText); // the honest limitation is preserved
    }

    /// <summary>
    /// Minimal offline service: these display contracts never touch the API.
    /// </summary>
    private sealed class OfflineCloudManagementService : ICloudManagementService
    {
        private readonly bool _signedIn;

        public OfflineCloudManagementService(bool signedIn) => _signedIn = signedIn;

        public bool IsConfiguredAndSignedIn => _signedIn;
        public string? CurrentUserAccountId => _signedIn ? "test-owner-account-id" : null;

        public event EventHandler? RegistrationChanged;
        public event EventHandler? SyncCompleted;

        public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudDeviceRegistration?>(null);

        public Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(
            string displayName, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Network, message: "offline"));

        public Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<DeviceListItem>>.Ok(Array.Empty<DeviceListItem>()));

        public Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "offline"));

        public Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "offline"));

        public Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(Array.Empty<AlarmGroupDto>()));

        public Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
            AlarmGroupWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<GroupSaveOutcome>.Fail(ApiErrorKind.Network, message: "offline"));

        public Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
            string groupId, AlarmGroupWriteRequest request, long ifMatchVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<GroupSaveOutcome>.Fail(ApiErrorKind.Network, message: "offline"));

        public Task<ApiResult> DeleteAlarmGroupAsync(
            string groupId, long ifMatchVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "offline"));

        public Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesiredSyncOutcome(
                DesiredSyncStatus.TransportFailed, null, null, ApiErrorKind.Network, "offline"));
    }
}
