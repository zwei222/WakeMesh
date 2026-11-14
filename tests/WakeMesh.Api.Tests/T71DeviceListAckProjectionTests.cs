using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WakeMesh.Api.Tests;

/// <summary>
/// T71: the owner device list must carry a projected "has a successful sync ACK" signal
/// so the UI can distinguish "no successful sync yet" (false) from "old server, unknown" (null).
/// Exercises the real API/JSON producer over SQLite (no DTO/substitute stand-ins).
/// </summary>
public sealed class T71DeviceListAckProjectionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public T71DeviceListAckProjectionTests(ApiFactory factory) => _factory = factory;

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
        client.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        return client;
    }

    private static async Task<(Guid Id, string Credential)> RegisterAsync(HttpClient owner, string name)
    {
        var res = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("credential").GetString()!);
    }

    private static async Task<string> ListRawAsync(HttpClient owner)
    {
        var res = await owner.GetAsync("/api/v1/devices");
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"list status {(int)res.StatusCode}: {text}");
        return text;
    }

    private static JsonElement FindDevice(string listJson, Guid id)
    {
        using var doc = JsonDocument.Parse(listJson);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == id)
            {
                return item.Clone();
            }
        }

        Assert.Fail($"device {id} missing from owner list");
        return default;
    }

    private static async Task<(Guid ReceiptId, long Revision)> GetDesiredAsync(HttpClient deviceClient, Guid deviceId)
    {
        var res = await deviceClient.GetAsync($"/api/v1/devices/{deviceId}/desired");
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"desired status {(int)res.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        return (body.GetProperty("receiptId").GetGuid(), body.GetProperty("revision").GetInt64());
    }

    private static async Task<HttpStatusCode> AckAsync(
        HttpClient deviceClient, Guid deviceId, Guid receiptId, long revision, string result)
    {
        var res = await deviceClient.PostAsJsonAsync(
            $"/api/v1/devices/{deviceId}/acks",
            new { receiptId, revision, result, failures = Array.Empty<object>(), clientReportedAt = (DateTimeOffset?)null });
        return res.StatusCode;
    }

    [Fact]
    public async Task List_ForNeverAckedDevice_ReportsHasSuccessfulSyncFalse()
    {
        var owner = Owner();
        var (id, _) = await RegisterAsync(owner, "t71-fresh");

        var listJson = await ListRawAsync(owner);
        var entry = FindDevice(listJson, id);

        Assert.True(entry.TryGetProperty("hasSuccessfulSync", out var flag),
            $"hasSuccessfulSync missing from list item: {entry.GetRawText()}");
        Assert.Equal(JsonValueKind.False, flag.ValueKind);
    }

    [Fact]
    public async Task List_AfterRev0SuccessAck_ReportsTrue_EvenWhenAppliedStaysZero()
    {
        var owner = Owner();
        var (id, credential) = await RegisterAsync(owner, "t71-rev0-success");
        var device = DeviceClient(credential);

        var (receiptId, revision) = await GetDesiredAsync(device, id);
        Assert.Equal(0, revision);

        var ackStatus = await AckAsync(device, id, receiptId, revision, "success");
        Assert.Equal(HttpStatusCode.OK, ackStatus);

        var listJson = await ListRawAsync(owner);
        var entry = FindDevice(listJson, id);

        Assert.True(entry.TryGetProperty("hasSuccessfulSync", out var flag),
            $"hasSuccessfulSync missing from list item: {entry.GetRawText()}");
        Assert.Equal(JsonValueKind.True, flag.ValueKind);
        // The bug scenario: successful rev0 ACK leaves both revisions at 0.
        Assert.Equal(0, entry.GetProperty("desiredRevision").GetInt64());
        Assert.Equal(0, entry.GetProperty("appliedRevision").GetInt64());

        // Projection only — no receipt/credential/owner identifiers in the owner list.
        Assert.DoesNotContain("receiptId", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(credential, listJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_AfterFailureAckOnly_ReportsFalse()
    {
        var owner = Owner();
        var (id, credential) = await RegisterAsync(owner, "t71-failure-only");
        var device = DeviceClient(credential);

        var (receiptId, revision) = await GetDesiredAsync(device, id);
        var ackStatus = await AckAsync(device, id, receiptId, revision, "failure");
        Assert.Equal(HttpStatusCode.OK, ackStatus);

        var entry = FindDevice(await ListRawAsync(owner), id);
        Assert.True(entry.TryGetProperty("hasSuccessfulSync", out var flag),
            $"hasSuccessfulSync missing from list item: {entry.GetRawText()}");
        Assert.Equal(JsonValueKind.False, flag.ValueKind);
    }

    [Fact]
    public async Task List_LaterFailureAck_DoesNotUndoPriorSuccess()
    {
        var owner = Owner();
        var (id, credential) = await RegisterAsync(owner, "t71-success-then-failure");
        var device = DeviceClient(credential);

        var (receiptId, revision) = await GetDesiredAsync(device, id);
        Assert.Equal(HttpStatusCode.OK, await AckAsync(device, id, receiptId, revision, "success"));
        Assert.Equal(HttpStatusCode.OK, await AckAsync(device, id, receiptId, revision, "failure"));

        var entry = FindDevice(await ListRawAsync(owner), id);
        Assert.Equal(JsonValueKind.True, entry.GetProperty("hasSuccessfulSync").ValueKind);
    }

    [Fact]
    public async Task List_IgnoresAcksFromPreviousRegistrationGeneration()
    {
        var owner = Owner();

        // Generation 1: same logical device, successful rev0 ACK.
        var (oldId, oldCredential) = await RegisterAsync(owner, "t71-regeneration");
        var oldDevice = DeviceClient(oldCredential);
        var (oldReceipt, oldRevision) = await GetDesiredAsync(oldDevice, oldId);
        Assert.Equal(HttpStatusCode.OK, await AckAsync(oldDevice, oldId, oldReceipt, oldRevision, "success"));

        // Generation 2: re-enrollment produces a new server device row; it has no ACK of its own.
        var (newId, newCredential) = await RegisterAsync(owner, "t71-regeneration");
        Assert.NotEqual(oldId, newId);

        var listJson = await ListRawAsync(owner);
        Assert.Equal(JsonValueKind.True, FindDevice(listJson, oldId).GetProperty("hasSuccessfulSync").ValueKind);
        Assert.Equal(JsonValueKind.False, FindDevice(listJson, newId).GetProperty("hasSuccessfulSync").ValueKind);

        // New generation's own valid success ACK flips only its row.
        var newDevice = DeviceClient(newCredential);
        var (newReceipt, newRevision) = await GetDesiredAsync(newDevice, newId);
        Assert.Equal(HttpStatusCode.OK, await AckAsync(newDevice, newId, newReceipt, newRevision, "success"));

        var afterJson = await ListRawAsync(owner);
        Assert.Equal(JsonValueKind.True, FindDevice(afterJson, newId).GetProperty("hasSuccessfulSync").ValueKind);
    }

    [Fact]
    public async Task List_SuccessAck_IsNotProjectedFromForeignReceiptOrUnrelatedDevice()
    {
        var owner = Owner();
        var (ackedId, ackedCredential) = await RegisterAsync(owner, "t71-acked");
        var (otherId, _) = await RegisterAsync(owner, "t71-bystander");

        var ackedDevice = DeviceClient(ackedCredential);
        var (receiptId, revision) = await GetDesiredAsync(ackedDevice, ackedId);
        Assert.Equal(HttpStatusCode.OK, await AckAsync(ackedDevice, ackedId, receiptId, revision, "success"));

        var listJson = await ListRawAsync(owner);
        Assert.Equal(JsonValueKind.True, FindDevice(listJson, ackedId).GetProperty("hasSuccessfulSync").ValueKind);
        Assert.Equal(JsonValueKind.False, FindDevice(listJson, otherId).GetProperty("hasSuccessfulSync").ValueKind);
    }

    /// <summary>
    /// Provider gate: the projection query (IN over device ids + receipt join + DISTINCT) must run
    /// unchanged on the production PostgreSQL provider, not just on SQLite.
    /// </summary>
    [PostgresFact]
    public async Task List_ProjectsAckEvidence_OnRealPostgres()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var database = await cluster.CreateDatabaseAsync("wm_t71");
        await database.MigrateAsync();

        await using var api = ApiFactory.CreateFor(
            providerOverride: "Postgres",
            connectionStringOverride: database.ApiConnectionString,
            skipDatabaseInitialize: true);

        var owner = api.CreateClient();
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", api.Auth.CreateAccessToken());

        var (ackedId, credential) = await RegisterAsync(owner, "t71-pg-acked");
        var (freshId, _) = await RegisterAsync(owner, "t71-pg-fresh");

        var entryBefore = FindDevice(await ListRawAsync(owner), ackedId);
        Assert.Equal(JsonValueKind.False, entryBefore.GetProperty("hasSuccessfulSync").ValueKind);

        var device = api.CreateClient();
        device.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", api.Auth.CreateAccessToken());
        device.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        var (receiptId, revision) = await GetDesiredAsync(device, ackedId);
        Assert.Equal(0, revision);
        Assert.Equal(HttpStatusCode.OK, await AckAsync(device, ackedId, receiptId, revision, "success"));

        var listJson = await ListRawAsync(owner);
        var acked = FindDevice(listJson, ackedId);
        Assert.Equal(JsonValueKind.True, acked.GetProperty("hasSuccessfulSync").ValueKind);
        Assert.Equal(0, acked.GetProperty("appliedRevision").GetInt64());
        Assert.Equal(JsonValueKind.False, FindDevice(listJson, freshId).GetProperty("hasSuccessfulSync").ValueKind);
    }
}
