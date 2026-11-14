using WakeMesh.Client.Models;

namespace WakeMesh.Client.Tests;

public sealed class ClientPushTokenTests : IClassFixture<ClientApiFactory>
{
    private readonly ClientApiFactory _factory;

    public ClientPushTokenTests(ClientApiFactory factory) => _factory = factory;

    private (WakeMeshApiClient Client, MemoryDeviceCredentialStore Creds) CreateSut()
    {
        var http = _factory.CreateClient();
        var tokens = new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken());
        var creds = new MemoryDeviceCredentialStore();
        var client = WakeMeshApiClient.CreateForTests(http, tokens, creds);
        return (client, creds);
    }

    [Fact]
    public async Task PutAndDeletePushToken_RoundTrip_AndOtherDeviceRejected()
    {
        var (client, _) = CreateSut();
        var a = await client.RegisterDeviceAsync("client-push-a");
        var b = await client.RegisterDeviceAsync("client-push-b");
        Assert.True(a.IsSuccess && b.IsSuccess);

        const string token = "client-fcm-token-xyz";
        var put = await client.PutPushTokenAsync(a.Value!.Id, token);
        Assert.True(put.IsSuccess, $"put kind={put.Kind} code={put.ErrorCode}");
        Assert.Equal(a.Value.Id, put.Value!.DeviceId);
        Assert.NotEqual(default, put.Value.UpdatedAt);

        // Overload with capability true remains compatible.
        var putCap = await client.PutPushTokenAsync(a.Value.Id, token + "-2", notificationsCapable: true);
        Assert.True(putCap.IsSuccess, $"putCap kind={putCap.Kind} code={putCap.ErrorCode}");

        // Wrong device credential path
        // Point B's credential at A's id by swapping store
        var (client2, creds2) = CreateSut();
        // Register fresh and force wrong binding
        var x = await client2.RegisterDeviceAsync("client-push-x");
        var y = await client2.RegisterDeviceAsync("client-push-y");
        Assert.True(x.IsSuccess && y.IsSuccess);
        creds2.Set(x.Value!.Id, y.Value!.Credential);
        var bad = await client2.PutPushTokenAsync(x.Value.Id, "tok");
        Assert.False(bad.IsSuccess);
        Assert.Equal(ApiErrorKind.Unauthorized, bad.Kind);

        var del = await client.DeletePushTokenAsync(a.Value.Id);
        Assert.True(del.IsSuccess, $"del kind={del.Kind} code={del.ErrorCode}");
    }

    [Fact]
    public async Task CreateGroup_WhenFcmNotConfigured_NotificationIsNotConfigured()
    {
        var (client, _) = CreateSut();
        var reg = await client.RegisterDeviceAsync("client-push-nc");
        Assert.True(reg.IsSuccess);
        var create = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            Id: "client-push-group-nc",
            Label: "L",
            Enabled: true,
            Repeat: "weekly",
            Days: new[] { 1 },
            Date: null,
            TimeZone: "Asia/Tokyo",
            ExcludeHolidays: false,
            ExcludedDates: Array.Empty<string>(),
            Targets: new[]
            {
                new AlarmTargetDto(null, reg.Value!.Id, "07:00", "default", 80, true, true),
            }));
        Assert.True(create.IsSuccess);
        Assert.Equal("notConfigured", create.Value!.Notification.State);
    }
}
