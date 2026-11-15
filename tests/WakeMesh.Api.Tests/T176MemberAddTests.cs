using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Tests;

/// <summary>
/// T176 — adding a device to an existing alarm group must be a normal 200 save.
///
/// Regression for the false 412: <c>AlarmGroupService.ReplaceAsync</c> created the new
/// <see cref="DeviceScheduleEntity"/> with a pre-assigned <c>Guid</c> and only linked it through the
/// navigation collection. EF change detection therefore tracked it as <c>Modified</c>, issued an
/// <c>UPDATE ... WHERE Id = @new</c> (0 rows) and surfaced a <c>DbUpdateConcurrencyException</c> that the
/// service maps to 412 <c>concurrencyConflict</c> — indistinguishable from a real concurrent edit.
///
/// These tests run against the seeded SQLite test host (<see cref="ApiFactory"/>), i.e. the same
/// in-process API + real persistence path used by the rest of the API suite; no mock transport.
/// </summary>
public sealed class T176MemberAddTests : IClassFixture<ApiFactory>
{
    private const string GroupId = "t176-group-add";

    private readonly ApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public T176MemberAddTests(ApiFactory factory) => _factory = factory;

    private HttpClient CreateOwnerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Auth.CreateAccessToken());
        return client;
    }

    private HttpClient CreateDeviceClient(string credential)
    {
        var client = CreateOwnerClient();
        client.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        return client;
    }

    [Fact]
    public async Task T176_ReplaceGroup_AddMember_SavesWith200_AndKeepsExistingTarget()
    {
        var owner = CreateOwnerClient();
        var d1 = await RegisterAsync(owner, "t176-one");
        var d2 = await RegisterAsync(owner, "t176-two");

        // --- create group with d1 only (this path already works today) ---
        var created = await owner.PostAsJsonAsync("/api/v1/alarm-groups",
            Payload(GroupId, "Weekday", Target(d1.Id, "06:30", "chime", 80, true)));
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create status {(int)created.StatusCode}: {createdText}");

        var createdBody = JsonDocument.Parse(createdText).RootElement;
        var createdGroup = createdBody.GetProperty("group");
        Assert.Equal(1, createdGroup.GetProperty("version").GetInt64());
        Assert.Equal(1, createdGroup.GetProperty("targets").GetArrayLength());

        var d1Target = createdGroup.GetProperty("targets")[0];
        var d1ScheduleId = d1Target.GetProperty("id").GetGuid();
        Assert.Equal(d1.Id, d1Target.GetProperty("deviceId").GetGuid());
        Assert.Equal("06:30", d1Target.GetProperty("time").GetString());
        Assert.Equal("chime", d1Target.GetProperty("soundKey").GetString());
        Assert.Equal(80, d1Target.GetProperty("volume").GetInt32());

        var revsBefore = await ReadDesiredRevisionsAsync(owner, d1.Id, d2.Id);
        var outboxBefore = await CountOutboxAsync();
        var tombstonesBefore = await CountTombstonesAsync();

        // --- the regression: add d2, keep d1 exactly as stored, correct If-Match ---
        var addRes = await PutAsync(owner, GroupId, ifMatch: 1,
            Payload(GroupId, "Weekday",
                Target(d1.Id, "06:30", "chime", 80, true),
                Target(d2.Id, "07:00", "default", 70, false)));
        var addText = await addRes.Content.ReadAsStringAsync();
        Assert.True(addRes.StatusCode == HttpStatusCode.OK,
            $"member-add PUT must be 200, got {(int)addRes.StatusCode}: {addText}");

        var addBody = JsonDocument.Parse(addText).RootElement;
        var group = addBody.GetProperty("group");
        Assert.Equal(2, group.GetProperty("version").GetInt64());
        Assert.Equal(2, group.GetProperty("targets").GetArrayLength());

        var targets = group.GetProperty("targets").EnumerateArray().ToList();
        var keptTarget = targets.Single(t => t.GetProperty("deviceId").GetGuid() == d1.Id);
        var addedTarget = targets.Single(t => t.GetProperty("deviceId").GetGuid() == d2.Id);

        // existing target row identity + settings preserved
        Assert.Equal(d1ScheduleId, keptTarget.GetProperty("id").GetGuid());
        Assert.Equal("06:30", keptTarget.GetProperty("time").GetString());
        Assert.Equal("chime", keptTarget.GetProperty("soundKey").GetString());
        Assert.Equal(80, keptTarget.GetProperty("volume").GetInt32());
        Assert.True(keptTarget.GetProperty("vibrate").GetBoolean());

        // new target got a server-side id and the requested settings
        var addedScheduleId = addedTarget.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, addedScheduleId);
        Assert.NotEqual(d1ScheduleId, addedScheduleId);
        Assert.Equal("07:00", addedTarget.GetProperty("time").GetString());
        Assert.Equal(70, addedTarget.GetProperty("volume").GetInt32());
        Assert.False(addedTarget.GetProperty("vibrate").GetBoolean());

        // --- persistence: the new row is an INSERT with the returned id, not a phantom UPDATE ---
        var rows = await WithDbAsync(db => db.DeviceSchedules.AsNoTracking()
            .Where(s => s.GroupId == GroupId)
            .ToListAsync());
        Assert.Equal(2, rows.Count);
        var addedRow = rows.Single(r => r.DeviceId == d2.Id);
        Assert.Equal(addedScheduleId, addedRow.Id);
        Assert.Equal("07:00", addedRow.Time);
        Assert.Equal(70, addedRow.Volume);
        Assert.False(addedRow.Vibrate);
        Assert.Equal(d1ScheduleId, rows.Single(r => r.DeviceId == d1.Id).Id);

        // both devices bumped exactly once, and each new/kept row is stamped to its device revision
        var revsAfter = await ReadDesiredRevisionsAsync(owner, d1.Id, d2.Id);
        Assert.Equal(revsBefore[d1.Id] + 1, revsAfter[d1.Id]);
        Assert.Equal(revsBefore[d2.Id] + 1, revsAfter[d2.Id]);
        Assert.Equal(revsAfter[d1.Id], rows.Single(r => r.DeviceId == d1.Id).ModifiedRevision);
        Assert.Equal(revsAfter[d2.Id], addedRow.ModifiedRevision);

        // --- the added device sees the group item in its desired snapshot ---
        using (var d2Client = CreateDeviceClient(d2.Credential))
        {
            var desired = await d2Client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d2.Id}/desired", JsonOpts);
            Assert.Equal(revsAfter[d2.Id], desired.GetProperty("revision").GetInt64());
            var items = desired.GetProperty("items").EnumerateArray().ToList();
            var item = Assert.Single(items);
            Assert.Equal(addedScheduleId, item.GetProperty("scheduleId").GetGuid());
            Assert.Equal(GroupId, item.GetProperty("groupId").GetString());
            Assert.Equal("Weekday", item.GetProperty("label").GetString());
            Assert.Equal("07:00", item.GetProperty("time").GetString());
            Assert.Equal(revsAfter[d2.Id], item.GetProperty("modifiedRevision").GetInt64());
            Assert.Equal(0, desired.GetProperty("tombstones").GetArrayLength());
        }

        // d1 keeps a single item pointing at the original schedule row
        using (var d1Client = CreateDeviceClient(d1.Credential))
        {
            var desired = await d1Client.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d1.Id}/desired", JsonOpts);
            Assert.Equal(revsAfter[d1.Id], desired.GetProperty("revision").GetInt64());
            var item = Assert.Single(desired.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(d1ScheduleId, item.GetProperty("scheduleId").GetGuid());
            Assert.Equal("06:30", item.GetProperty("time").GetString());
            Assert.Equal(revsAfter[d1.Id], item.GetProperty("modifiedRevision").GetInt64());
        }

        // no phantom tombstones, no leaked outbox rows (FCM unconfigured in the test host)
        Assert.Equal(tombstonesBefore, await CountTombstonesAsync());
        Assert.Equal(outboxBefore, await CountOutboxAsync());

        // --- repeating the same members PUT stays a pure update (200, no extra rows) ---
        var resave = await PutAsync(owner, GroupId, ifMatch: 2,
            Payload(GroupId, "Weekday",
                Target(d1.Id, "06:45", "chime", 80, true),
                Target(d2.Id, "07:00", "default", 70, false)));
        var resaveText = await resave.Content.ReadAsStringAsync();
        Assert.True(resave.StatusCode == HttpStatusCode.OK,
            $"repeat members PUT must be 200, got {(int)resave.StatusCode}: {resaveText}");
        var resavedGroup = JsonDocument.Parse(resaveText).RootElement.GetProperty("group");
        Assert.Equal(3, resavedGroup.GetProperty("version").GetInt64());
        Assert.Equal(2, resavedGroup.GetProperty("targets").GetArrayLength());

        var rowsAfterResave = await WithDbAsync(db => db.DeviceSchedules.AsNoTracking()
            .Where(s => s.GroupId == GroupId)
            .ToListAsync());
        Assert.Equal(2, rowsAfterResave.Count);
        Assert.Equal(d1ScheduleId, rowsAfterResave.Single(r => r.DeviceId == d1.Id).Id);
        Assert.Equal(addedScheduleId, rowsAfterResave.Single(r => r.DeviceId == d2.Id).Id);
        Assert.Equal("06:45", rowsAfterResave.Single(r => r.DeviceId == d1.Id).Time);

        // --- a genuinely stale If-Match is still 412 and changes nothing ---
        var revsBeforeStale = await ReadDesiredRevisionsAsync(owner, d1.Id, d2.Id);
        var outboxBeforeStale = await CountOutboxAsync();
        var tombstonesBeforeStale = await CountTombstonesAsync();

        var stale = await PutAsync(owner, GroupId, ifMatch: 1,
            Payload(GroupId, "Weekday",
                Target(d1.Id, "05:00", "chime", 55, true),
                Target(d2.Id, "05:30", "default", 55, false)));
        var staleText = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var staleError = JsonDocument.Parse(staleText).RootElement.GetProperty("error").GetString();
        Assert.Equal("versionMismatch", staleError);

        var groups = await owner.GetFromJsonAsync<JsonElement>("/api/v1/alarm-groups", JsonOpts);
        var current = groups.EnumerateArray().Single(g => g.GetProperty("id").GetString() == GroupId);
        Assert.Equal(3, current.GetProperty("version").GetInt64());
        var currentTargets = current.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(2, currentTargets.Count);
        Assert.Equal(d1ScheduleId, currentTargets.Single(t => t.GetProperty("deviceId").GetGuid() == d1.Id).GetProperty("id").GetGuid());
        Assert.Equal(addedScheduleId, currentTargets.Single(t => t.GetProperty("deviceId").GetGuid() == d2.Id).GetProperty("id").GetGuid());
        Assert.Equal("06:45", currentTargets.Single(t => t.GetProperty("deviceId").GetGuid() == d1.Id).GetProperty("time").GetString());

        var rowsAfterStale = await WithDbAsync(db => db.DeviceSchedules.AsNoTracking()
            .Where(s => s.GroupId == GroupId)
            .ToListAsync());
        Assert.Equal(2, rowsAfterStale.Count);
        Assert.Equal("06:45", rowsAfterStale.Single(r => r.DeviceId == d1.Id).Time);
        Assert.Equal(revsBeforeStale, await ReadDesiredRevisionsAsync(owner, d1.Id, d2.Id));
        Assert.Equal(tombstonesBeforeStale, await CountTombstonesAsync());
        Assert.Equal(outboxBeforeStale, await CountOutboxAsync());
    }

    /// <summary>
    /// A rejected replace (real optimistic-concurrency loss inside SaveChanges) must return 412 and leave
    /// group/target/device/outbox state untouched: the unit of work runs inside one transaction
    /// (BeginTransaction -> SaveChanges -> Commit), so a failed save is never partially committed.
    /// </summary>
    [Fact]
    public async Task T176_ReplaceGroup_FailedSave_Returns412_AndLeavesGroupTargetsDevicesAndOutboxUnchanged()
    {
        var owner = CreateOwnerClient();
        var d1 = await RegisterAsync(owner, "t176-rb-one");
        var d2 = await RegisterAsync(owner, "t176-rb-two");
        const string groupId = "t176-group-rollback";

        var created = await owner.PostAsJsonAsync("/api/v1/alarm-groups",
            Payload(groupId, "Rollback", Target(d1.Id, "06:30", "default", 80, true)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var revsBefore = await ReadDesiredRevisionsAsync(owner, d1.Id, d2.Id);
        var outboxBefore = await CountOutboxAsync();
        var tombstonesBefore = await CountTombstonesAsync();

        var request = new AlarmGroupWriteRequest(
            groupId, "Rollback", true, "weekly", new[] { 1, 2, 3, 4, 5 }, null, "Asia/Tokyo", true,
            Array.Empty<string>(),
            new[]
            {
                new AlarmTargetDto(null, d1.Id, "06:30", "default", 80, true, true),
                new AlarmTargetDto(null, d2.Id, "07:00", "default", 70, false, true),
            });

        int status;
        object body;
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var groups = scope.ServiceProvider.GetRequiredService<AlarmGroupService>();

            // Track the group with its current token, then move the stored token behind that snapshot so the
            // unit of work's UPDATE loses the optimistic check (a real DbUpdateConcurrencyException).
            _ = await db.AlarmGroups.Include(g => g.Targets).FirstAsync(g => g.Id == groupId);
            using (var tamperScope = _factory.Services.CreateScope())
            {
                var tamperDb = tamperScope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
                var affected = await tamperDb.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AlarmGroups\" SET \"RowVersion\" = \"RowVersion\" + 7 WHERE \"Id\" = {0}", groupId);
                Assert.Equal(1, affected);
            }

            (status, body) = await groups.ReplaceAsync(groupId, request, ifMatch: 1, CancellationToken.None);
        }

        Assert.Equal(412, status);
        Assert.Equal("concurrencyConflict", body.GetType().GetProperty("error")?.GetValue(body) as string);

        // Nothing leaked out of the aborted transaction.
        var groupRow = await WithDbAsync(db => db.AlarmGroups.AsNoTracking().SingleAsync(g => g.Id == groupId));
        Assert.Equal(1, groupRow.Version);
        Assert.Equal("Rollback", groupRow.Label);

        var rows = await WithDbAsync(db => db.DeviceSchedules.AsNoTracking()
            .Where(s => s.GroupId == groupId)
            .ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal(d1.Id, row.DeviceId);
        Assert.Equal("06:30", row.Time);

        Assert.Equal(revsBefore, await ReadDesiredRevisionsAsync(owner, d1.Id, d2.Id));
        Assert.Equal(outboxBefore, await CountOutboxAsync());
        Assert.Equal(tombstonesBefore, await CountTombstonesAsync());

        // The group is still usable afterwards: a correct If-Match adds the member and bumps version by exactly 1.
        var retry = await PutAsync(owner, groupId, ifMatch: 1,
            Payload(groupId, "Rollback",
                Target(d1.Id, "06:30", "default", 80, true),
                Target(d2.Id, "07:00", "default", 70, false)));
        var retryText = await retry.Content.ReadAsStringAsync();
        Assert.True(retry.StatusCode == HttpStatusCode.OK,
            $"retry after rejected save must be 200, got {(int)retry.StatusCode}: {retryText}");
        Assert.Equal(2, JsonDocument.Parse(retryText).RootElement.GetProperty("group").GetProperty("version").GetInt64());
    }

    // ---------- helpers ----------

    private static object Payload(string id, string label, params object[] targets) => new
    {
        id,
        label,
        enabled = true,
        repeat = "weekly",
        days = new[] { 1, 2, 3, 4, 5 },
        timeZone = "Asia/Tokyo",
        excludeHolidays = true,
        excludedDates = Array.Empty<string>(),
        targets
    };

    private static object Target(Guid deviceId, string time, string soundKey, int volume, bool vibrate) => new
    {
        deviceId,
        time,
        soundKey,
        volume,
        vibrate,
        enabled = true
    };

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string groupId, long ifMatch, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/alarm-groups/{groupId}")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return client.SendAsync(request);
    }

    private static async Task<(Guid Id, string Credential)> RegisterAsync(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/v1/devices", new { displayName = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("credential").GetString()!);
    }

    private static async Task<Dictionary<Guid, long>> ReadDesiredRevisionsAsync(HttpClient owner, params Guid[] deviceIds)
    {
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices", JsonOpts);
        var wanted = deviceIds.ToHashSet();
        return list.EnumerateArray()
            .Where(d => wanted.Contains(d.GetProperty("id").GetGuid()))
            .ToDictionary(d => d.GetProperty("id").GetGuid(), d => d.GetProperty("desiredRevision").GetInt64());
    }

    private async Task<T> WithDbAsync<T>(Func<WakeMeshDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        return await action(db);
    }

    private Task<int> CountOutboxAsync()
        => WithDbAsync(db => db.PushOutbox.AsNoTracking().CountAsync());

    private Task<int> CountTombstonesAsync()
        => WithDbAsync(db => db.Tombstones.AsNoTracking().CountAsync());
}
