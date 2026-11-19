using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WakeMesh.Api.Tests;

public sealed class DeviceAndGroupTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public DeviceAndGroupTests(ApiFactory factory) => _factory = factory;

    private HttpClient CreateOwnerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Auth.CreateAccessToken());
        return client;
    }

    [Fact]
    public async Task RegisterDevice_ReturnsCredentialOnce_AndHashIsNotEchoedLater()
    {
        var client = CreateOwnerClient();
        var res = await client.PostAsJsonAsync("/api/v1/devices", new { displayName = "alpha-test" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.True(body.TryGetProperty("id", out var idEl));
        Assert.True(body.TryGetProperty("credential", out var credEl));
        var credential = credEl.GetString();
        Assert.False(string.IsNullOrWhiteSpace(credential));
        var raw = Convert.FromBase64String(credential!);
        Assert.Equal(32, raw.Length);

        var listRes = await client.GetAsync("/api/v1/devices");
        var listText = await listRes.Content.ReadAsStringAsync();
        Assert.True(listRes.IsSuccessStatusCode, $"list status {(int)listRes.StatusCode}: {listText}");
        Assert.DoesNotContain(credential!, listText);
    }

    [Fact]
    public async Task Desired_RequiresOwnerTokenAndDeviceCredential()
    {
        var owner = CreateOwnerClient();
        var created = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = "beta-test" });
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var id = device.GetProperty("id").GetGuid();
        var credential = device.GetProperty("credential").GetString()!;

        // Owner token only
        var noCred = await owner.GetAsync($"/api/v1/devices/{id}/desired");
        Assert.Equal(HttpStatusCode.Unauthorized, noCred.StatusCode);

        // Credential only
        var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        var noToken = await anon.GetAsync($"/api/v1/devices/{id}/desired");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // Wrong credential
        owner.DefaultRequestHeaders.Remove("X-Device-Credential");
        owner.DefaultRequestHeaders.Add("X-Device-Credential", Convert.ToBase64String(new byte[32]));
        var bad = await owner.GetAsync($"/api/v1/devices/{id}/desired");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // Both ok
        owner.DefaultRequestHeaders.Remove("X-Device-Credential");
        owner.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        var ok = await owner.GetAsync($"/api/v1/devices/{id}/desired");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var snap = await ok.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(id, snap.GetProperty("deviceId").GetGuid());
        Assert.True(snap.TryGetProperty("receiptId", out _));
    }

    [Fact]
    public async Task AlarmGroup_Crud_IfMatch_Tombstone_AndAckFlow()
    {
        var client = CreateOwnerClient();
        var d1 = await RegisterAsync(client, "dev-one");
        var d2 = await RegisterAsync(client, "dev-two");

        var createPayload = new
        {
            id = "alarm-sample-1",
            label = "Weekday",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1, 2, 3, 4, 5 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = true,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = d1.Id, time = "06:30", soundKey = "default", volume = 80, vibrate = true, enabled = true },
                new { deviceId = d2.Id, time = "07:00", soundKey = "default", volume = 70, vibrate = false, enabled = true },
            }
        };

        var created = await client.PostAsJsonAsync("/api/v1/alarm-groups", createPayload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("notConfigured", createdBody.GetProperty("notification").GetProperty("state").GetString());
        var group = createdBody.GetProperty("group");
        Assert.Equal(1, group.GetProperty("version").GetInt64());

        // Idempotent same content
        var again = await client.PostAsJsonAsync("/api/v1/alarm-groups", createPayload);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Conflict different content same id
        var conflictPayload = createPayload with { label = "Other" };
        // anonymous type can't with - rebuild
        var conflict = await client.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "alarm-sample-1",
            label = "Other",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1, 2, 3, 4, 5 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = true,
            excludedDates = Array.Empty<string>(),
            targets = createPayload.targets
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // PUT without If-Match -> 428
        var putNoMatch = await client.PutAsJsonAsync("/api/v1/alarm-groups/alarm-sample-1", new
        {
            id = "alarm-sample-1",
            label = "Weekday",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1, 2, 3, 4, 5 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = true,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = d1.Id, time = "06:45", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            }
        });
        Assert.Equal((HttpStatusCode)428, putNoMatch.StatusCode);

        // PUT wrong version -> 412
        var req412 = new HttpRequestMessage(HttpMethod.Put, "/api/v1/alarm-groups/alarm-sample-1")
        {
            Content = JsonContent.Create(new
            {
                id = "alarm-sample-1",
                label = "Weekday",
                enabled = true,
                repeat = "weekly",
                days = new[] { 1, 2, 3, 4, 5 },
                timeZone = "Asia/Tokyo",
                excludeHolidays = true,
                excludedDates = Array.Empty<string>(),
                targets = new object[]
                {
                    new { deviceId = d1.Id, time = "06:45", soundKey = "default", volume = 80, vibrate = true, enabled = true },
                }
            })
        };
        req412.Headers.TryAddWithoutValidation("If-Match", "999");
        var res412 = await client.SendAsync(req412);
        Assert.Equal(HttpStatusCode.PreconditionFailed, res412.StatusCode);

        // PUT replace targets (drop d2) with correct version
        var reqPut = new HttpRequestMessage(HttpMethod.Put, "/api/v1/alarm-groups/alarm-sample-1")
        {
            Content = JsonContent.Create(new
            {
                id = "alarm-sample-1",
                label = "Weekday",
                enabled = true,
                repeat = "weekly",
                days = new[] { 1, 2, 3, 4, 5 },
                timeZone = "Asia/Tokyo",
                excludeHolidays = true,
                excludedDates = Array.Empty<string>(),
                targets = new object[]
                {
                    new { deviceId = d1.Id, time = "06:45", soundKey = "default", volume = 80, vibrate = true, enabled = true },
                }
            })
        };
        reqPut.Headers.TryAddWithoutValidation("If-Match", "1");
        var resPut = await client.SendAsync(reqPut);
        Assert.Equal(HttpStatusCode.OK, resPut.StatusCode);
        var putBody = await resPut.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(2, putBody.GetProperty("group").GetProperty("version").GetInt64());

        // d2 desired should include tombstone
        client.DefaultRequestHeaders.Remove("X-Device-Credential");
        client.DefaultRequestHeaders.Add("X-Device-Credential", d2.Credential);
        var desired2 = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d2.Id}/desired");
        Assert.True(desired2.GetProperty("tombstones").GetArrayLength() >= 1);
        Assert.Equal(0, desired2.GetProperty("items").GetArrayLength());
        var receipt2 = desired2.GetProperty("receiptId").GetGuid();
        var rev2 = desired2.GetProperty("revision").GetInt64();

        // ACK success advances appliedRevision
        var ack = await client.PostAsJsonAsync($"/api/v1/devices/{d2.Id}/acks", new
        {
            receiptId = receipt2,
            revision = rev2,
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var ackBody = await ack.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(rev2, ackBody.GetProperty("appliedRevision").GetInt64());

        // Same success then failure must not roll back
        var ackFail = await client.PostAsJsonAsync($"/api/v1/devices/{d2.Id}/acks", new
        {
            receiptId = receipt2,
            revision = rev2,
            result = "failure",
            failures = new[] { new { scheduleId = (Guid?)null, code = "applyFailed", message = "x" } }
        });
        Assert.Equal(HttpStatusCode.OK, ackFail.StatusCode);
        var ackFailBody = await ackFail.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(rev2, ackFailBody.GetProperty("appliedRevision").GetInt64());

        // Future revision
        var future = await client.PostAsJsonAsync($"/api/v1/devices/{d2.Id}/acks", new
        {
            receiptId = receipt2,
            revision = rev2 + 100,
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);

        // Unknown receipt
        var unknown = await client.PostAsJsonAsync($"/api/v1/devices/{d2.Id}/acks", new
        {
            receiptId = Guid.NewGuid(),
            revision = rev2,
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        // Other device credential against d2 path
        client.DefaultRequestHeaders.Remove("X-Device-Credential");
        client.DefaultRequestHeaders.Add("X-Device-Credential", d1.Credential);
        var cross = await client.GetAsync($"/api/v1/devices/{d2.Id}/desired");
        Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);

        // d1 desired has item
        client.DefaultRequestHeaders.Remove("X-Device-Credential");
        client.DefaultRequestHeaders.Add("X-Device-Credential", d1.Credential);
        var desired1 = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d1.Id}/desired");
        Assert.Equal(1, desired1.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task DeleteGroup_RequiresIfMatch_AndEmitsTombstones()
    {
        var client = CreateOwnerClient();
        var d1 = await RegisterAsync(client, "dev-del");
        var create = await client.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "alarm-del-1",
            label = "Temp",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new object[]
            {
                new { deviceId = d1.Id, time = "05:00", soundKey = "default", volume = 50, vibrate = true, enabled = true },
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var delNo = await client.DeleteAsync("/api/v1/alarm-groups/alarm-del-1");
        Assert.Equal((HttpStatusCode)428, delNo.StatusCode);

        var delReq = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/alarm-groups/alarm-del-1");
        delReq.Headers.TryAddWithoutValidation("If-Match", "1");
        var del = await client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        client.DefaultRequestHeaders.Add("X-Device-Credential", d1.Credential);
        var desired = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d1.Id}/desired");
        Assert.Equal(0, desired.GetProperty("items").GetArrayLength());
        Assert.True(desired.GetProperty("tombstones").GetArrayLength() >= 1);
    }

    private static async Task<(Guid Id, string Credential)> RegisterAsync(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/v1/devices", new { displayName = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("credential").GetString()!);
    }
}
