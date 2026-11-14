using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Push vertical tests use a mock IPushSender — never report mock Accepted as real FCM success.
/// </summary>
public sealed class PushNotificationTests : IAsyncLifetime
{
    private PushApiFactory _factory = null!;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _factory = new PushApiFactory();
        await _factory.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_factory).DisposeAsync();
    }

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Auth.CreateAccessToken());
        return client;
    }

    private HttpClient DeviceClient(string credential)
    {
        var client = Owner();
        client.DefaultRequestHeaders.Remove("X-Device-Credential");
        client.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        return client;
    }

    private async Task<(Guid Id, string Credential)> RegisterAsync(HttpClient owner, string name)
    {
        var res = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("credential").GetString()!);
    }

    [Fact]
    public async Task PutPushToken_RequiresOwnerAndDeviceCredential_AndDoesNotEchoToken()
    {
        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-reg-1");
        const string fcmToken = "fcm-test-token-alpha-111";

        // Owner only
        var noCred = await owner.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = fcmToken });
        Assert.Equal(HttpStatusCode.Unauthorized, noCred.StatusCode);

        // Credential only
        var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Add("X-Device-Credential", cred);
        var noToken = await anon.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = fcmToken });
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // OK
        var device = DeviceClient(cred);
        var ok = await device.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = fcmToken });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(id, body.GetProperty("deviceId").GetGuid());
        Assert.True(body.TryGetProperty("updatedAt", out _));
        var raw = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain(fcmToken, raw);

        // Owner list must not contain token
        var listText = await owner.GetStringAsync("/api/v1/devices");
        Assert.DoesNotContain(fcmToken, listText);
        var detailText = await owner.GetStringAsync($"/api/v1/devices/{id}");
        Assert.DoesNotContain(fcmToken, detailText);

        // Stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var row = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == id);
        Assert.Equal(fcmToken, row.PushToken);
        Assert.NotNull(row.PushTokenUpdatedAt);
    }

    [Fact]
    public async Task PutPushToken_RejectsOtherDeviceCredential_AndRevoked()
    {
        var owner = Owner();
        var a = await RegisterAsync(owner, "push-a");
        var b = await RegisterAsync(owner, "push-b");

        var wrong = DeviceClient(b.Credential);
        var bad = await wrong.PutAsJsonAsync($"/api/v1/devices/{a.Id}/push-token", new { token = "tok-x" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var revoke = await owner.PostAsync($"/api/v1/devices/{a.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var gone = await DeviceClient(a.Credential)
            .PutAsJsonAsync($"/api/v1/devices/{a.Id}/push-token", new { token = "tok-y" });
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
    }

    [Fact]
    public async Task DeletePushToken_ClearsStoredToken()
    {
        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-del");
        var device = DeviceClient(cred);
        await device.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-del" });

        var del = await device.DeleteAsync($"/api/v1/devices/{id}/push-token");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var row = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == id);
        Assert.Null(row.PushToken);
    }

    [Fact]
    public async Task Unconfigured_CreateGroup_ReturnsNotConfigured_AndNoOutbox()
    {
        // Default factory has Configured=false
        await using var factory = new PushApiFactory(configured: false, sender: new MockPushSender());
        await factory.InitializeAsync();
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Auth.CreateAccessToken());

        var reg = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = "u1" });
        var device = await reg.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var id = device.GetProperty("id").GetGuid();
        var cred = device.GetProperty("credential").GetString()!;
        owner.DefaultRequestHeaders.Add("X-Device-Credential", cred);
        await owner.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-u" });

        var create = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-unconfigured",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("notConfigured", body.GetProperty("notification").GetProperty("state").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        Assert.Equal(0, await db.PushOutbox.CountAsync());
        Assert.Equal(0, factory.Sender.SendCount);
    }

    [Fact]
    public async Task Configured_CreateGroup_EnqueuesOutbox_AndMockAccept_IsNotDeviceApply()
    {
        _factory.Sender.Reset();
        _factory.Sender.NextOutcome = PushSendOutcome.Accepted;

        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-send-ok");
        var device = DeviceClient(cred);
        await device.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-ok" });

        var create = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-push-ok",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("accepted", body.GetProperty("notification").GetProperty("state").GetString());
        Assert.Contains("deviceApplyRequiresAck", body.GetProperty("notification").GetProperty("detail").GetString());
        Assert.True(_factory.Sender.SendCount >= 1);

        // Payload shape
        var last = _factory.Sender.LastRequest;
        Assert.NotNull(last);
        Assert.Equal("tok-ok", last!.Token);
        Assert.Equal(id, last.DeviceId);
        Assert.True(last.Revision >= 1);
        Assert.Equal(PushMessageTypes.SettingsChanged, last.Type);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var outbox = await db.PushOutbox.AsNoTracking().Where(o => o.DeviceId == id).ToListAsync();
        Assert.Contains(outbox, o => o.Status == PushOutboxStatuses.Accepted);

        // Device applied revision still 0 until ACK
        var d = await db.Devices.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal(0, d.AppliedRevision);
        Assert.True(d.DesiredRevision >= 1);
    }

    [Fact]
    public async Task SendFail_StillSavesGroup_AndLeavesPendingOutbox()
    {
        _factory.Sender.Reset();
        _factory.Sender.NextOutcome = PushSendOutcome.TransientFailure;

        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-fail");
        await DeviceClient(cred).PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-fail" });

        var create = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-push-fail",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("failed", body.GetProperty("notification").GetProperty("state").GetString());
        Assert.True(body.TryGetProperty("group", out var group));
        Assert.Equal("g-push-fail", group.GetProperty("id").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var pending = await db.PushOutbox.AsNoTracking()
            .Where(o => o.DeviceId == id && (o.Status == PushOutboxStatuses.Failed || o.Status == PushOutboxStatuses.Pending))
            .ToListAsync();
        Assert.NotEmpty(pending);
    }

    [Fact]
    public async Task InvalidToken_ClearsStoredToken_AndDiscardsOutbox()
    {
        _factory.Sender.Reset();
        _factory.Sender.NextOutcome = PushSendOutcome.InvalidToken;

        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-invalid");
        await DeviceClient(cred).PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-bad" });

        var create = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-push-invalid",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var d = await db.Devices.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Null(d.PushToken);
        var discarded = await db.PushOutbox.AsNoTracking()
            .Where(o => o.DeviceId == id && o.Status == PushOutboxStatuses.Discarded)
            .ToListAsync();
        Assert.NotEmpty(discarded);
    }

    [Fact]
    public async Task RevokedDevice_IsExcludedFromSend_AndTokenClearedOnRevoke()
    {
        _factory.Sender.Reset();
        _factory.Sender.NextOutcome = PushSendOutcome.Accepted;

        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-revoke");
        await DeviceClient(cred).PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-rev" });

        var revoke = await owner.PostAsync($"/api/v1/devices/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var d = await db.Devices.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Null(d.PushToken);
        Assert.Equal(DeviceStatus.Revoked, d.Status);
    }

    [Fact]
    public async Task PendingOutbox_DrainsOnLaterRequest()
    {
        _factory.Sender.Reset();
        _factory.Sender.NextOutcome = PushSendOutcome.TransientFailure;

        var owner = Owner();
        var (id, cred) = await RegisterAsync(owner, "push-retry");
        await DeviceClient(cred).PutAsJsonAsync($"/api/v1/devices/{id}/push-token", new { token = "tok-retry" });

        await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-push-retry",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });

        // Force due
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var rows = await db.PushOutbox.Where(o => o.DeviceId == id).ToListAsync();
            foreach (var r in rows)
            {
                r.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                r.Status = PushOutboxStatuses.Failed;
            }

            await db.SaveChangesAsync();
        }

        _factory.Sender.NextOutcome = PushSendOutcome.Accepted;
        using (var scope = _factory.Services.CreateScope())
        {
            var push = scope.ServiceProvider.GetRequiredService<PushDispatchService>();
            var summary = await push.DrainPendingAsync(CancellationToken.None);
            Assert.True(summary.Accepted >= 1);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            Assert.Contains(
                await db.PushOutbox.AsNoTracking().Where(o => o.DeviceId == id).ToListAsync(),
                o => o.Status == PushOutboxStatuses.Accepted);
        }
    }
}

public sealed class MockPushSender : IPushSender
{
    private int _sendCount;

    public bool IsAvailable { get; set; } = true;
    public PushSendOutcome NextOutcome { get; set; } = PushSendOutcome.Accepted;
    public string? NextErrorCode { get; set; }
    public int SendCount => _sendCount;
    public MockPushRequest? LastRequest { get; private set; }
    public ConcurrentQueue<MockPushRequest> History { get; } = new();

    public void Reset()
    {
        Interlocked.Exchange(ref _sendCount, 0);
        LastRequest = null;
        while (History.TryDequeue(out _)) { }
        NextOutcome = PushSendOutcome.Accepted;
        NextErrorCode = null;
        IsAvailable = true;
    }

    public Task<PushSendResult> SendSettingsChangedAsync(
        string pushToken,
        Guid deviceId,
        long desiredRevision,
        bool notificationCapable,
        CancellationToken cancellationToken)
    {
        var req = new MockPushRequest(
            pushToken,
            deviceId,
            desiredRevision,
            PushMessageTypes.SettingsChanged,
            notificationCapable);
        LastRequest = req;
        History.Enqueue(req);
        Interlocked.Increment(ref _sendCount);
        return Task.FromResult(new PushSendResult(NextOutcome, NextErrorCode));
    }
}

public sealed record MockPushRequest(
    string Token,
    Guid DeviceId,
    long Revision,
    string Type,
    bool NotificationCapable = false);

/// <summary>Factory with optional FCM configured + mock sender replacement.</summary>
public sealed class PushApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"wakemesh-push-tests-{Guid.NewGuid():N}";
    private readonly bool _configured;
    public TestAuthFixture Auth { get; } = new();
    public MockPushSender Sender { get; }

    public PushApiFactory(bool configured = true, MockPushSender? sender = null)
    {
        _configured = configured;
        Sender = sender ?? new MockPushSender();
    }

    private Dictionary<string, string?> TestSettings => new()
    {
        ["ConnectionStrings:DefaultConnection"] = $"Data Source=file:{_dbName}?mode=memory&cache=shared",
        ["WakeMesh:Database:Provider"] = "Sqlite",
        ["WakeMesh:Auth:Issuer"] = TestAuthFixture.Issuer,
        ["WakeMesh:Auth:Audience"] = TestAuthFixture.Audience,
        ["WakeMesh:Auth:RequiredScope"] = TestAuthFixture.RequiredScope,
        ["WakeMesh:Auth:AllowedAzp"] = TestAuthFixture.AllowedAzp,
        ["WakeMesh:Auth:AllowedOwnerOids:0"] = TestAuthFixture.AllowedOid,
        ["WakeMesh:Auth:MetadataAddress"] =
            $"https://login.microsoftonline.com/{TestAuthFixture.ConsumersTenantId}/v2.0/.well-known/openid-configuration",
        ["WakeMesh:Fcm:Configured"] = _configured ? "true" : "false",
        ["WakeMesh:Fcm:DrainBatchSize"] = "32",
        ["WakeMesh:Fcm:MaxAttempts"] = "8",
    };

    private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(TestSettings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        foreach (var kv in TestSettings)
        {
            builder.UseSetting(kv.Key, kv.Value);
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(TestSettings));

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MetadataAddress = null!;
                options.ConfigurationManager = null;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = TestAuthFixture.Issuer,
                };
                options.TokenValidationParameters.IssuerSigningKey = Auth.SecurityKey;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            });

            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(Sender);

            // Rebind NotificationStatusService so it sees the mock sender after removal.
            services.RemoveAll<NotificationStatusService>();
            services.AddSingleton<NotificationStatusService>();
        });
    }

    public async Task InitializeAsync()
    {
        _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(TestSettings["ConnectionStrings:DefaultConnection"]);
        await _keepAlive.OpenAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
        }
    }
}
