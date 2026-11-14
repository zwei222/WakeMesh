using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Tests;

/// <summary>
/// T19 server-push fix probes: rotation-safe token clear, outbox lease claim,
/// capability→priority, schedule validation, bounded drain recovery notes.
/// </summary>
public sealed class T19ServerPushFixTests
{
    private static WebApplicationFactory<Program> Replace(PushApiFactory source, IPushSender sender) =>
        source.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IPushSender>();
            s.AddSingleton(sender);
        }));

    private static HttpClient Owner(WebApplicationFactory<Program> app, PushApiFactory source)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", source.Auth.CreateAccessToken());
        return c;
    }

    private static async Task<(Guid Id, HttpClient Device)> Register(
        WebApplicationFactory<Program> app,
        PushApiFactory source,
        HttpClient owner,
        string token = "synthetic-original",
        bool? notificationsCapable = null)
    {
        var r = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = "probe" });
        r.EnsureSuccessStatusCode();
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var id = j.GetProperty("id").GetGuid();
        var c = Owner(app, source);
        c.DefaultRequestHeaders.Add("X-Device-Credential", j.GetProperty("credential").GetString());
        object body = notificationsCapable is null
            ? new { token }
            : new { token, notificationsCapable };
        (await c.PutAsJsonAsync($"/api/v1/devices/{id}/push-token", body)).EnsureSuccessStatusCode();
        return (id, c);
    }

    private static Task<HttpResponseMessage> Group(HttpClient owner, Guid id, string groupId = "probe-group") =>
        owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = groupId,
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new[]
            {
                new
                {
                    deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true,
                },
            },
        });

    [Fact]
    public async Task RotationDuringSend_InvalidToken_MustPreserveReplacement()
    {
        await using var source = new PushApiFactory();
        await source.InitializeAsync();
        var sender = new GateSender(PushSendOutcome.InvalidToken);
        await using var app = Replace(source, sender);
        using var owner = Owner(app, source);
        var (id, device) = await Register(app, source, owner);
        using (device)
        {
            var saving = Group(owner, id);
            await sender.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await device.PutAsJsonAsync(
                    $"/api/v1/devices/{id}/push-token",
                    new { token = "synthetic-replacement" }))
                .EnsureSuccessStatusCode();
            sender.Release[0].SetResult();
            Assert.Equal(HttpStatusCode.Created, (await saving).StatusCode);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var row = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
            Assert.True(
                row.PushToken == "synthetic-replacement",
                "Replacement was erased after the older send returned InvalidToken.");
        }
    }

    [Fact]
    public async Task RequestAndWorker_MustNotConcurrentlySendSameOutbox()
    {
        await using var source = new PushApiFactory();
        await source.InitializeAsync();
        var sender = new GateSender(PushSendOutcome.Accepted);
        await using var app = Replace(source, sender);
        using var owner = Owner(app, source);
        var (id, device) = await Register(app, source, owner);
        using var disposeDevice = device;
        var saving = Group(owner, id);
        await sender.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var scope = app.Services.CreateScope();
        var draining = scope.ServiceProvider.GetRequiredService<PushDispatchService>()
            .DrainPendingAsync(default);
        // Second enter must time out — only claim winner sends.
        var secondEnter = sender.Entered[1].Task;
        var raced = await Task.WhenAny(secondEnter, Task.Delay(TimeSpan.FromMilliseconds(400)));
        Assert.NotSame(secondEnter, raced);
        sender.Release[0].SetResult();
        Assert.Equal(HttpStatusCode.Created, (await saving).StatusCode);
        await draining;
        using var checkScope = app.Services.CreateScope();
        var db = checkScope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        Assert.Equal(1, await db.PushOutbox.CountAsync());
        Assert.Equal(1, (await db.PushOutbox.SingleAsync()).AttemptCount);
        Assert.Equal(1, sender.Count);
    }

    [Fact]
    public async Task LeaseExpired_AllowsRetry_AndLateOwnerCompletionIsIgnored()
    {
        await using var source = new PushApiFactory();
        await source.InitializeAsync();
        var sender = new GateSender(PushSendOutcome.Accepted);
        await using var app = Replace(source, sender);
        using var owner = Owner(app, source);
        var (id, device) = await Register(app, source, owner);
        using var disposeDevice = device;

        // Seed one pending outbox without going through request drain (direct DB).
        Guid outboxId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            outboxId = Guid.NewGuid();
            db.PushOutbox.Add(new PushOutboxEntity
            {
                Id = outboxId,
                DeviceId = id,
                DesiredRevision = 1,
                MessageType = PushMessageTypes.SettingsChanged,
                Status = PushOutboxStatuses.Pending,
                AttemptCount = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        using var scopeA = app.Services.CreateScope();
        var drainA = scopeA.ServiceProvider.GetRequiredService<PushDispatchService>()
            .DrainPendingAsync(default);
        await sender.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Expire lease while owner A still in-flight.
        string? ownerA;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var row = await db.PushOutbox.SingleAsync(o => o.Id == outboxId);
            ownerA = row.LeaseOwner;
            Assert.NotNull(ownerA);
            Assert.Equal(PushOutboxStatuses.Sending, row.Status);
            row.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        // Owner B reclaims and completes Accepted.
        using var scopeB = app.Services.CreateScope();
        var drainB = scopeB.ServiceProvider.GetRequiredService<PushDispatchService>()
            .DrainPendingAsync(default);
        await sender.Entered[1].Task.WaitAsync(TimeSpan.FromSeconds(10));
        sender.Release[1].SetResult();
        await drainB;

        // Late owner A completes with failure — must not overwrite B's accepted.
        sender.NextOutcome = PushSendOutcome.TransientFailure;
        sender.Release[0].SetResult();
        await drainA;

        using var check = app.Services.CreateScope();
        var finalDb = check.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        var final = await finalDb.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == outboxId);
        Assert.Equal(PushOutboxStatuses.Accepted, final.Status);
        Assert.Equal(2, final.AttemptCount);
        Assert.Null(final.LeaseOwner);
        Assert.Equal(2, sender.Count);
        Assert.NotNull(ownerA);
    }

    [Fact]
    public async Task NotificationsCapable_TrueUsesHigh_FalseUsesNormal()
    {
        await using var source = new PushApiFactory();
        await source.InitializeAsync();
        var sender = new RecordingPrioritySender();
        await using var app = Replace(source, sender);
        using var owner = Owner(app, source);

        var (idHi, devHi) = await Register(app, source, owner, "tok-hi", notificationsCapable: true);
        using (devHi)
        {
            Assert.Equal(HttpStatusCode.Created, (await Group(owner, idHi, "g-hi")).StatusCode);
        }

        var (idLo, devLo) = await Register(app, source, owner, "tok-lo", notificationsCapable: false);
        using (devLo)
        {
            Assert.Equal(HttpStatusCode.Created, (await Group(owner, idLo, "g-lo")).StatusCode);
        }

        // Omitted capable → default false
        var (idDef, devDef) = await Register(app, source, owner, "tok-def", notificationsCapable: null);
        using (devDef)
        {
            Assert.Equal(HttpStatusCode.Created, (await Group(owner, idDef, "g-def")).StatusCode);
        }

        Assert.Contains(sender.History, h => h.Token == "tok-hi" && h.NotificationCapable);
        Assert.Contains(sender.History, h => h.Token == "tok-lo" && !h.NotificationCapable);
        Assert.Contains(sender.History, h => h.Token == "tok-def" && !h.NotificationCapable);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        Assert.True(await db.Devices.AsNoTracking().Where(d => d.Id == idHi)
            .Select(d => d.PushNotificationsCapable).SingleAsync());
        Assert.False(await db.Devices.AsNoTracking().Where(d => d.Id == idLo)
            .Select(d => d.PushNotificationsCapable).SingleAsync());
    }

    [Fact]
    public async Task WeeklyDays_NullOrEmpty_Returns400_OnceStillWorks()
    {
        await using var source = new PushApiFactory();
        await source.InitializeAsync();
        using var owner = Owner(source, source);
        var reg = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = "v" });
        var id = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var nullDays = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-null-days",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = (int[]?)null,
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, nullDays.StatusCode);

        var emptyDays = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-empty-days",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = Array.Empty<int>(),
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyDays.StatusCode);

        var badTz = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-bad-tz",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Not/AZone",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTz.StatusCode);

        var badTime = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-bad-time",
            label = "L",
            enabled = true,
            repeat = "weekly",
            days = new[] { 1 },
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new[]
            {
                new { deviceId = id, time = "25:99", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);

        var once = await owner.PostAsJsonAsync("/api/v1/alarm-groups", new
        {
            id = "g-once-ok",
            label = "L",
            enabled = true,
            repeat = "once",
            date = "2030-01-15",
            timeZone = "Asia/Tokyo",
            excludeHolidays = false,
            excludedDates = Array.Empty<string>(),
            targets = new[]
            {
                new { deviceId = id, time = "07:00", soundKey = "default", volume = 80, vibrate = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.Created, once.StatusCode);
    }

    [Fact]
    public async Task ExceptionRetry_MaxAttempts_AndUnconfiguredEnqueue()
    {
        await using var source = new PushApiFactory();
        await source.InitializeAsync();
        var sender = new ThrowSender();
        await using var app = Replace(source, sender);
        using var owner = Owner(app, source);
        var (id, device) = await Register(app, source, owner);
        using var disposeDevice = device;
        Assert.Equal(HttpStatusCode.Created, (await Group(owner, id)).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var row = await db.PushOutbox.SingleAsync();
            Assert.Equal(PushOutboxStatuses.Failed, row.Status);
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal(nameof(InvalidOperationException), row.LastErrorCode);
            row.AttemptCount = 7;
            row.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PushDispatchService>().DrainPendingAsync(default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>().PushOutbox.SingleAsync();
            Assert.Equal(PushOutboxStatuses.Discarded, row.Status);
            Assert.Equal(8, row.AttemptCount);
            Assert.Null(row.NextAttemptAt);
        }

        foreach (var configured in new[] { false, true })
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var options = Options.Create(new FcmOptions
            {
                Configured = configured,
                CredentialsPath = "/nonexistent/t19-no-credential.json",
            });
            var unavailable = new FirebasePushSender(
                options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FirebasePushSender>.Instance);
            Assert.False(unavailable.IsAvailable);
            var push = new PushDispatchService(
                db,
                unavailable,
                new NotificationStatusService(options, unavailable),
                options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PushDispatchService>.Instance);
            var before = await db.PushOutbox.CountAsync();
            await push.EnqueueSettingsChangedAsync(new[] { id }, default);
            await db.SaveChangesAsync();
            Assert.Equal(before + (configured ? 1 : 0), await db.PushOutbox.CountAsync());
            Assert.Equal("notConfigured", (await push.NotifyAfterCommitAsync(default)).State);
        }
    }

    private sealed class ThrowSender : IPushSender
    {
        public bool IsAvailable => true;

        public Task<PushSendResult> SendSettingsChangedAsync(
            string value,
            Guid id,
            long revision,
            bool notificationCapable,
            CancellationToken ct) =>
            throw new InvalidOperationException("Synthetic transport failure (no confidential values).");
    }

    private sealed class RecordingPrioritySender : IPushSender
    {
        public bool IsAvailable => true;
        public List<MockPushRequest> History { get; } = new();

        public Task<PushSendResult> SendSettingsChangedAsync(
            string pushToken,
            Guid deviceId,
            long desiredRevision,
            bool notificationCapable,
            CancellationToken cancellationToken)
        {
            History.Add(new MockPushRequest(
                pushToken,
                deviceId,
                desiredRevision,
                PushMessageTypes.SettingsChanged,
                notificationCapable));
            return Task.FromResult(new PushSendResult(PushSendOutcome.Accepted));
        }
    }

    private sealed class GateSender(PushSendOutcome outcome) : IPushSender
    {
        private int _count;
        public PushSendOutcome NextOutcome { get; set; } = outcome;
        public int Count => _count;
        public bool IsAvailable => true;

        public TaskCompletionSource[] Entered { get; } =
            Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

        public TaskCompletionSource[] Release { get; } =
            Enumerable.Range(0, 4)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

        public async Task<PushSendResult> SendSettingsChangedAsync(
            string value,
            Guid id,
            long revision,
            bool notificationCapable,
            CancellationToken ct)
        {
            var i = Interlocked.Increment(ref _count) - 1;
            Entered[i].SetResult();
            await Release[i].Task.WaitAsync(TimeSpan.FromSeconds(20));
            return new PushSendResult(NextOutcome);
        }
    }
}
