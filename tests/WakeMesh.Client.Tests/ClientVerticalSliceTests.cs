using System.Net;
using WakeMesh.Client.Http;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;

namespace WakeMesh.Client.Tests;

public sealed class ClientVerticalSliceTests : IClassFixture<ClientApiFactory>
{
    private readonly ClientApiFactory _factory;

    public ClientVerticalSliceTests(ClientApiFactory factory) => _factory = factory;

    private (WakeMeshApiClient Client, StaticAccessTokenProvider Tokens, MemoryDeviceCredentialStore Creds) CreateSut(string? token = null)
    {
        var http = _factory.CreateClient();
        // TestServer transport is http://localhost — production path uses HTTPS factory separately.
        var tokens = new StaticAccessTokenProvider(token ?? _factory.Auth.CreateAccessToken());
        var creds = new MemoryDeviceCredentialStore();
        var client = WakeMeshApiClient.CreateForTests(http, tokens, creds);
        return (client, tokens, creds);
    }

    [Fact]
    public async Task Vertical_Register_Group_Desired_Ack_WithOwnerAndCredential()
    {
        var (client, _, creds) = CreateSut();

        var reg = await client.RegisterDeviceAsync("client-slice-a");
        Assert.True(reg.IsSuccess, $"register kind={reg.Kind} code={reg.ErrorCode}");
        Assert.NotNull(reg.Value);
        Assert.False(string.IsNullOrWhiteSpace(reg.Value!.Credential));
        var deviceId = reg.Value.Id;
        // Credential persisted via store
        Assert.Equal(reg.Value.Credential, await creds.GetCredentialAsync(deviceId));

        var create = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            Id: "client-group-1",
            Label: "Morning",
            Enabled: true,
            Repeat: "weekly",
            Days: new[] { 1, 2, 3, 4, 5 },
            Date: null,
            TimeZone: "Asia/Tokyo",
            ExcludeHolidays: true,
            ExcludedDates: Array.Empty<string>(),
            Targets: new[]
            {
                new AlarmTargetDto(null, deviceId, "06:30", "default", 80, true, true),
            }));
        Assert.True(create.IsSuccess, $"create kind={create.Kind} code={create.ErrorCode}");
        Assert.NotNull(create.Value);
        // Server save ≠ device applied
        Assert.Equal(1, create.Value!.Group.Version);
        Assert.Equal("notConfigured", create.Value.Notification.State);

        var desired = await client.GetDesiredAsync(deviceId);
        Assert.True(desired.IsSuccess, $"desired kind={desired.Kind} code={desired.ErrorCode}");
        Assert.NotNull(desired.Value);
        Assert.Equal(deviceId, desired.Value!.DeviceId);
        Assert.True(desired.Value.Items.Count >= 1);
        Assert.NotEqual(Guid.Empty, desired.Value.ReceiptId);

        var ack = await client.PostAckAsync(deviceId, new AckRequest(
            desired.Value.ReceiptId,
            desired.Value.Revision,
            "success",
            Array.Empty<AckFailureDto>(),
            null));
        Assert.True(ack.IsSuccess, $"ack kind={ack.Kind} code={ack.ErrorCode}");
        Assert.Equal(desired.Value.Revision, ack.Value!.AppliedRevision);

        var detail = await client.GetDeviceAsync(deviceId);
        Assert.True(detail.IsSuccess);
        Assert.Equal(desired.Value.Revision, detail.Value!.AppliedRevision);
    }

    [Fact]
    public async Task WrongDeviceCredential_IsUnauthorized_AndDistinguishedFromGone()
    {
        var (client, _, creds) = CreateSut();
        var a = await client.RegisterDeviceAsync("dev-a");
        var b = await client.RegisterDeviceAsync("dev-b");
        Assert.True(a.IsSuccess && b.IsSuccess);

        // Point path device A at credential of B
        creds.Set(a.Value!.Id, b.Value!.Credential);
        var bad = await client.GetDesiredAsync(a.Value.Id);
        Assert.False(bad.IsSuccess);
        Assert.Equal(ApiErrorKind.Unauthorized, bad.Kind);
        Assert.Equal(401, bad.StatusCode);

        // Restore correct credential, revoke, then desired → Gone (410)
        creds.Set(a.Value.Id, a.Value.Credential);
        var rev = await client.RevokeDeviceAsync(a.Value.Id);
        Assert.True(rev.IsSuccess, $"revoke kind={rev.Kind}");
        var gone = await client.GetDesiredAsync(a.Value.Id);
        Assert.False(gone.IsSuccess);
        Assert.Equal(ApiErrorKind.Gone, gone.Kind);
        Assert.Equal(410, gone.StatusCode);
        Assert.Equal("deviceRevoked", gone.ErrorCode);
    }

    [Fact]
    public async Task ReplaceGroup_WrongIfMatch_IsPreconditionFailed_412()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("etag-dev");
        var deviceId = reg.Value!.Id;

        var created = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "etag-group",
            "L",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "05:00", "default", 50, true, true) }));
        Assert.True(created.IsSuccess);

        var conflict = await client.ReplaceAlarmGroupAsync(
            "etag-group",
            new AlarmGroupWriteRequest(
                "etag-group",
                "L2",
                true,
                "weekly",
                new[] { 1 },
                null,
                "Asia/Tokyo",
                false,
                Array.Empty<string>(),
                new[] { new AlarmTargetDto(null, deviceId, "05:05", "default", 50, true, true) }),
            ifMatchVersion: 999);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(ApiErrorKind.PreconditionFailed, conflict.Kind);
        Assert.Equal(412, conflict.StatusCode);
        Assert.Equal("versionMismatch", conflict.ErrorCode);
    }

    [Fact]
    public async Task MissingToken_IsUnauthorized_ReLoginSignal()
    {
        var (client, tokens, _) = CreateSut();
        tokens.SetToken(null);
        var list = await client.ListDevicesAsync();
        Assert.False(list.IsSuccess);
        Assert.Equal(ApiErrorKind.Unauthorized, list.Kind);
        Assert.Equal(401, list.StatusCode);
    }

    [Fact]
    public async Task SyncOrchestrator_SuccessPath_AppliesThenAcks()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("sync-ok");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "sync-g1",
            "S",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:00", "default", 60, true, true) }));

        var applier = new RecordingApplier(_ => new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>()));
        var orch = new DesiredSyncOrchestrator(client, applier, new MemoryAppliedRevisionStore());
        var outcome = await orch.SyncDeviceAsync(deviceId);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, outcome.Status);
        Assert.NotNull(outcome.Snapshot);
        Assert.NotNull(outcome.Ack);
        Assert.Equal(1, applier.Calls);
        Assert.Equal(outcome.Snapshot!.Revision, outcome.Ack!.AppliedRevision);

        var detail = await client.GetDeviceAsync(deviceId);
        Assert.Equal(outcome.Snapshot.Revision, detail.Value!.AppliedRevision);
    }

    [Fact]
    public async Task SyncOrchestrator_ApplyFailure_SendsFailedAck_NotSuccess()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("sync-fail");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "sync-g-fail",
            "S",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:00", "default", 60, true, true) }));

        var applier = new RecordingApplier(_ => new DesiredApplyResult(
            Succeeded: false,
            Completed: true,
            Failures: new[] { new AckFailureDto(null, "osRegisterFailed", "stub") }));
        var orch = new DesiredSyncOrchestrator(client, applier, new MemoryAppliedRevisionStore());
        var outcome = await orch.SyncDeviceAsync(deviceId);

        Assert.Equal(DesiredSyncStatus.ApplyFailedAcknowledged, outcome.Status);
        Assert.NotNull(outcome.Ack);
        // Failed ACK does not advance applied revision on server for success path;
        // applied stays below desired unless a prior success existed.
        var detail = await client.GetDeviceAsync(deviceId);
        Assert.Equal(0, detail.Value!.AppliedRevision);
        Assert.True(detail.Value.DesiredRevision >= 1);
    }

    [Fact]
    public async Task SyncOrchestrator_CancelDuringApply_DoesNotSendAck()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("sync-cancel");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "sync-g-cancel",
            "S",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:00", "default", 60, true, true) }));

        using var cts = new CancellationTokenSource();
        var applier = new RecordingApplier(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var orch = new DesiredSyncOrchestrator(client, applier, new MemoryAppliedRevisionStore());
        var outcome = await orch.SyncDeviceAsync(deviceId, cts.Token);

        Assert.Equal(DesiredSyncStatus.CanceledNoAck, outcome.Status);
        Assert.Null(outcome.Ack);

        var detail = await client.GetDeviceAsync(deviceId);
        Assert.Equal(0, detail.Value!.AppliedRevision);
    }

    [Fact]
    public async Task SyncOrchestrator_IncompleteApply_DoesNotSendAck()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("sync-incomplete");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "sync-g-inc",
            "S",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:00", "default", 60, true, true) }));

        var applier = new RecordingApplier(_ => new DesiredApplyResult(false, Completed: false, Array.Empty<AckFailureDto>()));
        var orch = new DesiredSyncOrchestrator(client, applier, new MemoryAppliedRevisionStore());
        var outcome = await orch.SyncDeviceAsync(deviceId);

        Assert.Equal(DesiredSyncStatus.ApplyIncompleteNoAck, outcome.Status);
        Assert.Null(outcome.Ack);
        var detail = await client.GetDeviceAsync(deviceId);
        Assert.Equal(0, detail.Value!.AppliedRevision);
    }

    [Fact]
    public async Task RetireDevice_ThenListShowsRetirementState()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("retire-me");
        var id = reg.Value!.Id;
        var retire = await client.RetireDeviceAsync(id);
        Assert.True(retire.IsSuccess, $"retire kind={retire.Kind} code={retire.ErrorCode}");
        var detail = await client.GetDeviceAsync(id);
        Assert.True(detail.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(detail.Value!.RetirementState));
    }

    [Fact]
    public async Task FutureRevisionAck_IsUnprocessable_422()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("future-rev");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "future-group",
            "F",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:40", "default", 55, true, true) }));

        var desired = await client.GetDesiredAsync(deviceId);
        var res = await client.PostAckAsync(deviceId, new AckRequest(
            desired.Value!.ReceiptId,
            desired.Value.Revision + 100,
            "success",
            Array.Empty<AckFailureDto>(),
            null));
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.UnprocessableEntity, res.Kind);
        Assert.Equal(422, res.StatusCode);
        Assert.Equal("futureRevision", res.ErrorCode);
    }

    [Fact]
    public async Task DuplicateGroupId_DifferentContent_IsConflict_409()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("dup-dev");
        var deviceId = reg.Value!.Id;
        AlarmGroupWriteRequest Request(string label) => new(
            "dup-group",
            label,
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:50", "default", 55, true, true) });

        var first = await client.CreateAlarmGroupAsync(Request("A"));
        Assert.True(first.IsSuccess);
        var duplicate = await client.CreateAlarmGroupAsync(Request("B"));
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(ApiErrorKind.Conflict, duplicate.Kind);
        Assert.Equal("conflict", duplicate.ErrorCode);
    }

    private sealed class RecordingApplier : IDesiredApplier
    {
        private readonly Func<DesiredSnapshotDto, DesiredApplyResult> _fn;
        public int Calls { get; private set; }

        public RecordingApplier(Func<DesiredSnapshotDto, DesiredApplyResult> fn) => _fn = fn;

        public Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto snapshot, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_fn(snapshot));
        }
    }
}
