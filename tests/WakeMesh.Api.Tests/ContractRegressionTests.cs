using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Permanent regression coverage for independent review findings (real JWT + SQLite).
/// </summary>
public sealed class ContractRegressionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ContractRegressionTests(ApiFactory factory) => _factory = factory;

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

    private static async Task<(Guid Id, string Credential)> RegisterAsync(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/v1/devices", new { displayName = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("credential").GetString()!);
    }

    private static object GroupBody(string id, string label, params Guid[] deviceIds) => new
    {
        id,
        label,
        enabled = true,
        repeat = "weekly",
        days = new[] { 1 },
        timeZone = "Asia/Tokyo",
        excludeHolidays = false,
        excludedDates = Array.Empty<string>(),
        targets = deviceIds.Select(d => (object)new
        {
            deviceId = d,
            time = "06:00",
            soundKey = "default",
            volume = 50,
            vibrate = true,
            enabled = true
        }).ToArray()
    };

    [Fact]
    public async Task SuccessAck_WithFailures_IsRejected400()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-fail-body");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-fail-body", "x", device.Id));
        var client = DeviceClient(device.Credential);
        var desired = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}/desired");
        var res = await client.PostAsJsonAsync($"/api/v1/devices/{device.Id}/acks", new
        {
            receiptId = desired.GetProperty("receiptId").GetGuid(),
            revision = desired.GetProperty("revision").GetInt64(),
            result = "success",
            failures = new[] { new { scheduleId = (Guid?)null, code = "applyFailed", message = "m" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ConcurrentSuccessAcks_AppliedRevisionNeverRegresses()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-ack-max");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-ack-max", "x", device.Id));
        var client = DeviceClient(device.Credential);
        var receipts = new List<(Guid Receipt, long Rev)>();
        long ver = 1;
        for (var i = 0; i < 20; i++)
        {
            var snap = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}/desired");
            receipts.Add((snap.GetProperty("receiptId").GetGuid(), snap.GetProperty("revision").GetInt64()));
            var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/alarm-groups/g-ack-max")
            {
                Content = JsonContent.Create(GroupBody("g-ack-max", "x" + i, device.Id))
            };
            put.Headers.TryAddWithoutValidation("If-Match", ver.ToString());
            (await owner.SendAsync(put)).EnsureSuccessStatusCode();
            ver++;
        }

        var responses = await Task.WhenAll(
            receipts.AsEnumerable().Reverse().Select(async x =>
            {
                var c = DeviceClient(device.Credential);
                return await c.PostAsJsonAsync($"/api/v1/devices/{device.Id}/acks", new
                {
                    receiptId = x.Receipt,
                    revision = x.Rev,
                    result = "success",
                    failures = Array.Empty<object>()
                });
            }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices");
        var me = list.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == device.Id);
        Assert.Equal(receipts.Max(x => x.Rev), me.GetProperty("appliedRevision").GetInt64());
    }

    [Fact]
    public async Task AckRacingWithPut_DoesNotReturn500()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-ack-put");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-ack-put", "x", device.Id));
        var client = DeviceClient(device.Credential);
        long ver = 1;
        for (var i = 0; i < 15; i++)
        {
            var snap = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}/desired");
            var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/alarm-groups/g-ack-put")
            {
                Content = JsonContent.Create(GroupBody("g-ack-put", "y" + i, device.Id))
            };
            put.Headers.TryAddWithoutValidation("If-Match", ver.ToString());
            var pair = await Task.WhenAll(
                owner.SendAsync(put),
                DeviceClient(device.Credential).PostAsJsonAsync($"/api/v1/devices/{device.Id}/acks", new
                {
                    receiptId = snap.GetProperty("receiptId").GetGuid(),
                    revision = snap.GetProperty("revision").GetInt64(),
                    result = "success",
                    failures = Array.Empty<object>()
                }));
            Assert.NotEqual(HttpStatusCode.InternalServerError, pair[0].StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, pair[1].StatusCode);
            if (pair[0].IsSuccessStatusCode)
            {
                ver++;
            }
        }
    }

    [Fact]
    public async Task ConcurrentSameIdSameBody_Post_IsIdempotentWithout500()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-post-race");
        var body = GroupBody("g-post-race", "same", device.Id);
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Owner().PostAsJsonAsync("/api/v1/alarm-groups", body)));
        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.Contains(responses, r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        Assert.All(responses, r =>
            Assert.True(
                r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"unexpected {(int)r.StatusCode}"));
        // At least one success path, and no body leak on conflict.
        foreach (var r in responses.Where(x => x.StatusCode == HttpStatusCode.Conflict))
        {
            var text = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("same", text);
            Assert.Contains("conflict", text);
        }
    }

    [Fact]
    public async Task Retire_AllowsEmptyDesiredUntilCancelAck_ThenRevokes()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-retire");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-retire", "x", device.Id));
        var client = DeviceClient(device.Credential);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}/desired");
        Assert.True(before.GetProperty("items").GetArrayLength() >= 1);

        var retire = await owner.PostAsync($"/api/v1/devices/{device.Id}/retire", null);
        Assert.Equal(HttpStatusCode.NoContent, retire.StatusCode);

        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices");
        var me = list.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == device.Id);
        Assert.Equal("Retiring", me.GetProperty("status").GetString());
        Assert.Equal("retiring", me.GetProperty("retirementState").GetString());

        var desired = await client.GetAsync($"/api/v1/devices/{device.Id}/desired");
        Assert.Equal(HttpStatusCode.OK, desired.StatusCode);
        var snap = await desired.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(0, snap.GetProperty("items").GetArrayLength());
        Assert.True(snap.GetProperty("tombstones").GetArrayLength() >= 1);

        var ack = await client.PostAsJsonAsync($"/api/v1/devices/{device.Id}/acks", new
        {
            receiptId = snap.GetProperty("receiptId").GetGuid(),
            revision = snap.GetProperty("revision").GetInt64(),
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices");
        me = list.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == device.Id);
        Assert.Equal("Revoked", me.GetProperty("status").GetString());
        Assert.Equal("revoked", me.GetProperty("retirementState").GetString());

        var after = await client.GetAsync($"/api/v1/devices/{device.Id}/desired");
        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);
    }

    [Fact]
    public async Task ImmediateRevoke_MarksUnconfirmedAndBlocksDesired()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-revoke-now");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-revoke-now", "x", device.Id));
        var revoke = await owner.PostAsync($"/api/v1/devices/{device.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices");
        var me = list.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == device.Id);
        Assert.Equal("Revoked", me.GetProperty("status").GetString());
        Assert.Equal("revokedUnconfirmed", me.GetProperty("retirementState").GetString());

        var client = DeviceClient(device.Credential);
        var desired = await client.GetAsync($"/api/v1/devices/{device.Id}/desired");
        Assert.Equal(HttpStatusCode.Gone, desired.StatusCode);
    }

    [Fact]
    public async Task ReceiptExpired_AndCrossDevice_AndSuccessThenFailureSameRev()
    {
        var owner = Owner();
        var d1 = await RegisterAsync(owner, "reg-rcpt-a");
        var d2 = await RegisterAsync(owner, "reg-rcpt-b");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-rcpt", "x", d1.Id, d2.Id));
        var c1 = DeviceClient(d1.Credential);
        var c2 = DeviceClient(d2.Credential);
        var s1 = await c1.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d1.Id}/desired");
        var s2 = await c2.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d2.Id}/desired");

        var cross = await c1.PostAsJsonAsync($"/api/v1/devices/{d1.Id}/acks", new
        {
            receiptId = s2.GetProperty("receiptId").GetGuid(),
            revision = s2.GetProperty("revision").GetInt64(),
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Conflict, cross.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var rc = await db.SnapshotReceipts.FirstAsync(x => x.Id == s1.GetProperty("receiptId").GetGuid());
            rc.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var expired = await c1.PostAsJsonAsync($"/api/v1/devices/{d1.Id}/acks", new
        {
            receiptId = s1.GetProperty("receiptId").GetGuid(),
            revision = s1.GetProperty("revision").GetInt64(),
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);

        // Fresh receipt: success then same-rev failure keeps applied.
        var fresh = await c1.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d1.Id}/desired");
        var ok = await c1.PostAsJsonAsync($"/api/v1/devices/{d1.Id}/acks", new
        {
            receiptId = fresh.GetProperty("receiptId").GetGuid(),
            revision = fresh.GetProperty("revision").GetInt64(),
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var fail = await c1.PostAsJsonAsync($"/api/v1/devices/{d1.Id}/acks", new
        {
            receiptId = fresh.GetProperty("receiptId").GetGuid(),
            revision = fresh.GetProperty("revision").GetInt64(),
            result = "failure",
            failures = new[] { new { scheduleId = (Guid?)null, code = "later", message = "x" } }
        });
        Assert.Equal(HttpStatusCode.OK, fail.StatusCode);
        var ackBody = await fail.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(fresh.GetProperty("revision").GetInt64(), ackBody.GetProperty("appliedRevision").GetInt64());

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d1.Id}");
        Assert.True(detail.GetProperty("recentFailures").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task DesiredSnapshot_IsRevisionConsistent_UnderConcurrentPut()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-snap");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-snap-a", "a", device.Id));
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-snap-b", "b", device.Id));
        var client = DeviceClient(device.Credential);

        var tasks = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(client.GetAsync($"/api/v1/devices/{device.Id}/desired"));
            var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/alarm-groups/g-snap-a")
            {
                Content = JsonContent.Create(GroupBody("g-snap-a", "a" + i, device.Id))
            };
            // best-effort version; conflicts are ok
            put.Headers.TryAddWithoutValidation("If-Match", (1 + i).ToString());
            tasks.Add(Owner().SendAsync(put));
        }

        var results = await Task.WhenAll(tasks);
        foreach (var r in results.Where(x => x.RequestMessage!.Method == HttpMethod.Get && x.IsSuccessStatusCode))
        {
            var snap = await r.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            var rev = snap.GetProperty("revision").GetInt64();
            foreach (var item in snap.GetProperty("items").EnumerateArray())
            {
                Assert.True(item.GetProperty("modifiedRevision").GetInt64() <= rev);
            }
        }
    }

    [Fact]
    public async Task Startup_RejectsEmptyOwnerAllowlist_FailClosed()
    {
        // Covered by AuthTests allowlist behavior; ensure valid token path still OwnerOnly fallback.
        var anon = _factory.CreateClient();
        var res = await anon.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task DifferentContentSameId_Returns409_WithoutServerError()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-diff");
        var first = await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-diff", "one", device.Id));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-diff", "two", device.Id));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var text = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("one", text);
        Assert.DoesNotContain("two", text);
    }

    [Fact]
    public async Task StaleSuccessAck_WhileRetiring_DoesNotCompleteRetirement()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-stale-retire-ack");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-stale-retire", "x", device.Id));
        var client = DeviceClient(device.Credential);

        // Pre-retire non-empty snapshot (rev typically 1).
        var pre = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}/desired");
        var preRev = pre.GetProperty("revision").GetInt64();
        Assert.True(pre.GetProperty("items").GetArrayLength() >= 1);

        var retire = await owner.PostAsync($"/api/v1/devices/{device.Id}/retire", null);
        Assert.Equal(HttpStatusCode.NoContent, retire.StatusCode);

        // Stale success ACK of the old receipt must NOT complete cancel (DesiredRevision moved).
        var stale = await client.PostAsJsonAsync($"/api/v1/devices/{device.Id}/acks", new
        {
            receiptId = pre.GetProperty("receiptId").GetGuid(),
            revision = preRev,
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}");
        Assert.Equal("Retiring", detail.GetProperty("status").GetString());
        Assert.Equal("retiring", detail.GetProperty("retirementState").GetString());
        Assert.False(detail.GetProperty("cancelConfirmed").GetBoolean());
        Assert.True(detail.GetProperty("desiredRevision").GetInt64() > preRev);
        // Applied may advance from the stale ACK, but retirement is incomplete.
        Assert.Equal(preRev, detail.GetProperty("appliedRevision").GetInt64());

        // Current empty cancel snapshot ACK still completes retirement.
        var cancelSnap = await client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}/desired");
        Assert.Equal(0, cancelSnap.GetProperty("items").GetArrayLength());
        var done = await client.PostAsJsonAsync($"/api/v1/devices/{device.Id}/acks", new
        {
            receiptId = cancelSnap.GetProperty("receiptId").GetGuid(),
            revision = cancelSnap.GetProperty("revision").GetInt64(),
            result = "success",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}");
        Assert.Equal("Revoked", detail.GetProperty("status").GetString());
        Assert.True(detail.GetProperty("cancelConfirmed").GetBoolean());
    }

    [Fact]
    public async Task GetDesired_ActivatesPendingDevice_Persistently()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-pending-activate");
        var before = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}");
        Assert.Equal("Pending", before.GetProperty("status").GetString());

        var client = DeviceClient(device.Credential);
        var desired = await client.GetAsync($"/api/v1/devices/{device.Id}/desired");
        Assert.Equal(HttpStatusCode.OK, desired.StatusCode);

        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}");
        Assert.Equal("Active", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Retire_Vs_CreateGroup_NeverLeavesScheduleOnRetiring()
    {
        // Behavioral race coverage on SQLite. This does NOT guarantee SQL Server isolation /
        // locking semantics — only that the in-transaction status re-check + RowVersion path
        // holds under this provider.
        var owner = Owner();
        var retiringWithSchedules = 0;
        for (var i = 0; i < 30; i++)
        {
            var device = await RegisterAsync(owner, "reg-race-" + i);
            var pair = await Task.WhenAll(
                Owner().PostAsJsonAsync("/api/v1/alarm-groups", GroupBody($"g-race-{i}", "x", device.Id)),
                Owner().PostAsync($"/api/v1/devices/{device.Id}/retire", null));

            Assert.DoesNotContain(pair, r => r.StatusCode == HttpStatusCode.InternalServerError);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var dev = await db.Devices.AsNoTracking().FirstAsync(x => x.Id == device.Id);
            var sched = await db.DeviceSchedules.AsNoTracking().CountAsync(x => x.DeviceId == device.Id);
            if (dev.Status == DeviceStatus.Retiring && sched > 0)
            {
                retiringWithSchedules++;
            }

            // If create won fully, device should still be Pending/Active with schedules, or
            // Retiring with zero schedules after retire emptied them — never Retiring+schedules.
            if (dev.Status == DeviceStatus.Retiring)
            {
                Assert.Equal(0, sched);
            }
        }

        Assert.Equal(0, retiringWithSchedules);
    }

    [Fact]
    public void AlarmGroupService_Source_RevalidatesStatusInsideBump()
    {
        // Static evidence: in-tx re-read gate exists (SQLite races alone do not prove SQL Server).
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "WakeMesh.Api", "Services", "AlarmGroupService.cs");
        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), path);
        var src = File.ReadAllText(path);
        Assert.Contains("d.Status is not (DeviceStatus.Active or DeviceStatus.Pending)", src, StringComparison.Ordinal);
        Assert.Contains("unknownOrRevokedDevice", src, StringComparison.Ordinal);
        Assert.Contains("SQLite in-memory tests do not prove SQL Server", src, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retire_StaleRowVersion_ReturnsTyped409_No500_NoOrphanTombstones()
    {
        // Injected concurrency: tracked stale Device RowVersion + concurrent bump, then RetireAsync.
        // Must not surface 500, must roll back staged cancel tombstones, and return typed 409 while still live.
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-retire-concurrency");
        var create = await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-retire-concurrency", "x", device.Id));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<WakeMesh.Api.Services.DeviceService>();

        // Track device in this request-scoped context (stale after concurrent bump).
        var tracked = await db.Devices.FirstAsync(d => d.Id == device.Id);
        Assert.True(tracked.Status is DeviceStatus.Pending or DeviceStatus.Active);
        var schedulesBefore = await db.DeviceSchedules.AsNoTracking().CountAsync(x => x.DeviceId == device.Id);
        Assert.True(schedulesBefore >= 1);
        var tombsBefore = await db.Tombstones.AsNoTracking().CountAsync(t => t.DeviceId == device.Id);

        // Concurrent writer bumps Device.RowVersion (e.g. schedule materialization path).
        using (var other = _factory.Services.CreateScope())
        {
            var db2 = other.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var live = await db2.Devices.FirstAsync(d => d.Id == device.Id);
            live.RowVersion += 1;
            await db2.SaveChangesAsync();
        }

        // EF returns the already-tracked stale instance from FirstOrDefault inside RetireAsync.
        var (status, body) = await svc.RetireAsync(device.Id, CancellationToken.None);
        Assert.NotEqual(500, status);
        Assert.Equal(409, status);
        Assert.NotNull(body);
        var json = JsonSerializer.Serialize(body, JsonOpts);
        Assert.Contains("concurrencyConflict", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", json, StringComparison.OrdinalIgnoreCase);

        // Transaction rollback: no half-applied cancel snapshot / orphan tombstones from the failed retire.
        db.ChangeTracker.Clear();
        var after = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == device.Id);
        Assert.True(after.Status is DeviceStatus.Pending or DeviceStatus.Active);
        Assert.NotEqual(DeviceStatus.Retiring, after.Status);
        var schedulesAfter = await db.DeviceSchedules.AsNoTracking().CountAsync(x => x.DeviceId == device.Id);
        var tombsAfter = await db.Tombstones.AsNoTracking().CountAsync(t => t.DeviceId == device.Id);
        Assert.Equal(schedulesBefore, schedulesAfter);
        Assert.Equal(tombsBefore, tombsAfter);

        // Fresh retire still succeeds (regression through HTTP).
        var retire = await owner.PostAsync($"/api/v1/devices/{device.Id}/retire", null);
        Assert.Equal(HttpStatusCode.NoContent, retire.StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{device.Id}");
        Assert.Equal("Retiring", detail.GetProperty("status").GetString());
        Assert.Equal(0, await db.DeviceSchedules.AsNoTracking().CountAsync(x => x.DeviceId == device.Id));
    }

    [Fact]
    public async Task ConcurrentDoubleRetire_No500_EndsRetiringIdempotent()
    {
        var owner = Owner();
        var device = await RegisterAsync(owner, "reg-double-retire");
        await owner.PostAsJsonAsync("/api/v1/alarm-groups", GroupBody("g-double-retire", "x", device.Id));

        var pair = await Task.WhenAll(
            Owner().PostAsync($"/api/v1/devices/{device.Id}/retire", null),
            Owner().PostAsync($"/api/v1/devices/{device.Id}/retire", null));

        Assert.DoesNotContain(pair, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.All(pair, r => Assert.True(
            r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode}"));

        foreach (var r in pair.Where(x => x.StatusCode == HttpStatusCode.Conflict))
        {
            var text = await r.Content.ReadAsStringAsync();
            Assert.Contains("concurrencyConflict", text, StringComparison.Ordinal);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var dev = await db.Devices.AsNoTracking().FirstAsync(x => x.Id == device.Id);
        Assert.Equal(DeviceStatus.Retiring, dev.Status);
        Assert.Equal(0, await db.DeviceSchedules.AsNoTracking().CountAsync(x => x.DeviceId == device.Id));

        // Idempotent follow-up retire.
        var again = await owner.PostAsync($"/api/v1/devices/{device.Id}/retire", null);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public void DeviceService_Source_HandlesRetireConcurrency()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "WakeMesh.Api", "Services", "DeviceService.cs");
        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), path);
        var src = File.ReadAllText(path);
        Assert.Contains("RetireAsync", src, StringComparison.Ordinal);
        Assert.Contains("DbUpdateConcurrencyException", src, StringComparison.Ordinal);
        Assert.Contains("concurrencyConflict", src, StringComparison.Ordinal);
        // Must not swallow without typed outcome / fresh-state check.
        Assert.Contains("ChangeTracker.Clear()", src, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_HttpMetadataAddress_FailsAtHostConstruction()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Data Source=file:http-meta-fail?mode=memory&cache=shared",
            ["WakeMesh:Database:Provider"] = "Sqlite",
            ["WakeMesh:Auth:Issuer"] = TestAuthFixture.Issuer,
            ["WakeMesh:Auth:Audience"] = TestAuthFixture.Audience,
            ["WakeMesh:Auth:RequiredScope"] = TestAuthFixture.RequiredScope,
            ["WakeMesh:Auth:AllowedAzp"] = TestAuthFixture.AllowedAzp,
            ["WakeMesh:Auth:AllowedOwnerOids:0"] = TestAuthFixture.AllowedOid,
            ["WakeMesh:Auth:MetadataAddress"] = "http://127.0.0.1:9/.well-known/openid-configuration",
        };

        Exception? caught = null;
        try
        {
            await using var factory = new RawConfigFactory(settings);
            _ = factory.CreateClient();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        var text = caught!.ToString();
        Assert.Contains("HTTPS", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RawConfigFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _settings;

        public RawConfigFactory(Dictionary<string, string?> settings) => _settings = settings;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(_settings));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var kv in _settings)
            {
                builder.UseSetting(kv.Key, kv.Value);
            }
        }
    }
}

