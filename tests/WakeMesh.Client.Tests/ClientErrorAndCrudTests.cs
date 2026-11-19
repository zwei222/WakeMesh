using System.Net;
using System.Net.Http.Headers;
using WakeMesh.Client.Models;

namespace WakeMesh.Client.Tests;

public sealed class ClientErrorAndCrudTests : IClassFixture<ClientApiFactory>
{
    private readonly ClientApiFactory _factory;

    public ClientErrorAndCrudTests(ClientApiFactory factory) => _factory = factory;

    private (WakeMeshApiClient Client, StaticAccessTokenProvider Tokens, MemoryDeviceCredentialStore Creds) CreateSut(string? token = null)
    {
        var http = _factory.CreateClient();
        var tokens = new StaticAccessTokenProvider(token ?? _factory.Auth.CreateAccessToken());
        var creds = new MemoryDeviceCredentialStore();
        return (WakeMeshApiClient.CreateForTests(http, tokens, creds), tokens, creds);
    }

    [Fact]
    public async Task Forbidden_WhenTokenLacksScope_IsDistinctFromUnauthorized()
    {
        var (client, tokens, _) = CreateSut();
        tokens.SetToken(_factory.Auth.CreateAccessToken(scp: null));
        var res = await client.ListDevicesAsync();
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.Forbidden, res.Kind);
        Assert.Equal(403, res.StatusCode);

        tokens.SetToken(_factory.Auth.CreateAccessToken(scp: "User.Read"));
        var res2 = await client.ListDevicesAsync();
        Assert.Equal(ApiErrorKind.Forbidden, res2.Kind);
    }

    [Fact]
    public async Task GroupList_And_Delete_CompleteCrud()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("crud-dev");
        var deviceId = reg.Value!.Id;

        var created = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "crud-group",
            "C",
            true,
            "weekly",
            new[] { 2 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "07:00", "default", 40, false, true) }));
        Assert.True(created.IsSuccess);

        var list = await client.ListAlarmGroupsAsync();
        Assert.True(list.IsSuccess);
        Assert.Contains(list.Value!, g => g.Id == "crud-group");

        // The client always sends If-Match; a wrong version must surface as 412 (not silently overwrite).
        var noMatch = await client.DeleteAlarmGroupAsync("crud-group", ifMatchVersion: 0);
        Assert.False(noMatch.IsSuccess);
        Assert.Equal(ApiErrorKind.PreconditionFailed, noMatch.Kind);
        Assert.Equal(412, noMatch.StatusCode);

        var deleted = await client.DeleteAlarmGroupAsync("crud-group", ifMatchVersion: created.Value!.Group.Version);
        Assert.True(deleted.IsSuccess, $"delete kind={deleted.Kind} code={deleted.ErrorCode}");

        var after = await client.ListAlarmGroupsAsync();
        Assert.True(after.IsSuccess);
        Assert.DoesNotContain(after.Value!, g => g.Id == "crud-group" && g.DeletedAt is null);
    }

    [Fact]
    public async Task AuthFailure_Messages_DoNotContainTokenOrCredential()
    {
        var (client, _, creds) = CreateSut();
        var reg = await client.RegisterDeviceAsync("secret-dev");
        var deviceId = reg.Value!.Id;
        var credential = reg.Value.Credential;

        // Wrong credential
        creds.Set(deviceId, Convert.ToBase64String(new byte[32]));
        var bad = await client.GetDesiredAsync(deviceId);
        Assert.False(bad.IsSuccess);
        Assert.DoesNotContain(credential, bad.Message ?? "");
        Assert.DoesNotContain(credential, bad.ErrorCode ?? "");

        // Missing token
        var (anon, tokens, _) = CreateSut();
        tokens.SetToken(null);
        var unauthorized = await anon.ListDevicesAsync();
        Assert.Equal(ApiErrorKind.Unauthorized, unauthorized.Kind);
        Assert.DoesNotContain("Bearer", unauthorized.Message ?? "");
    }

    [Fact]
    public async Task SaveSuccess_DoesNotImplyDeviceApplied()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("pending-apply");
        var deviceId = reg.Value!.Id;

        var save = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "pending-group",
            "P",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:15", "default", 70, true, true) }));
        Assert.True(save.IsSuccess);

        var detail = await client.GetDeviceAsync(deviceId);
        Assert.True(detail.IsSuccess);
        // Server accepted the save; target device has not ACKed.
        Assert.True(detail.Value!.DesiredRevision >= 1);
        Assert.True(detail.Value.AppliedRevision < detail.Value.DesiredRevision);
    }

    [Fact]
    public async Task FailedAck_IsRecorded_AndDoesNotAdvanceAppliedRevision()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("ack-fail-record");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "ack-fail-group",
            "F",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:10", "default", 55, true, true) }));

        var desired = await client.GetDesiredAsync(deviceId);
        var failAck = await client.PostAckAsync(deviceId, new AckRequest(
            desired.Value!.ReceiptId,
            desired.Value.Revision,
            "failure",
            new[] { new AckFailureDto(null, "osRegisterFailed", "fixture") },
            null));
        Assert.True(failAck.IsSuccess, $"ack kind={failAck.Kind} code={failAck.ErrorCode}");

        var detail = await client.GetDeviceAsync(deviceId);
        Assert.True(detail.IsSuccess);
        Assert.Equal(0, detail.Value!.AppliedRevision);
        Assert.Contains(detail.Value.RecentFailures, f => f.Code == "osRegisterFailed");
    }

    [Fact]
    public async Task UnknownReceiptAck_IsConflict_409()
    {
        var (client, _, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("bad-receipt");
        var deviceId = reg.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "bad-receipt-group",
            "R",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:20", "default", 55, true, true) }));

        var desired = await client.GetDesiredAsync(deviceId);
        var res = await client.PostAckAsync(deviceId, new AckRequest(
            Guid.NewGuid(),
            desired.Value!.Revision,
            "success",
            Array.Empty<AckFailureDto>(),
            null));
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.Conflict, res.Kind);
        Assert.Equal("unknownReceipt", res.ErrorCode);
    }

    [Fact]
    public async Task CrossDeviceCredential_Ack_IsUnauthorized()
    {
        var (client, _, creds) = CreateSut();
        var a = await client.RegisterDeviceAsync("cross-a");
        var b = await client.RegisterDeviceAsync("cross-b");
        var aId = a.Value!.Id;
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "cross-group",
            "X",
            true,
            "weekly",
            new[] { 1 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, aId, "06:25", "default", 55, true, true) }));

        var desired = await client.GetDesiredAsync(aId);
        Assert.True(desired.IsSuccess);

        // Use B credential against A path
        creds.Set(aId, b.Value!.Credential);
        var res = await client.PostAckAsync(aId, new AckRequest(
            desired.Value!.ReceiptId,
            desired.Value.Revision,
            "success",
            Array.Empty<AckFailureDto>(),
            null));
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.Unauthorized, res.Kind);
        Assert.Equal(401, res.StatusCode);
    }
}
