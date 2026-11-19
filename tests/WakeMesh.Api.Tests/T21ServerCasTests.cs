using System.Data.Common;
using System.Net;
using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;
using Xunit.Abstractions;

namespace WakeMesh.Api.Tests;

/// <summary>
/// T21 permanent gates: expired-owner InvalidToken must not clear token after a newer
/// owner Accepted; stale pending snapshots must not reclaim past a future backoff.
/// </summary>
public sealed class T21ServerCasTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(PushSendOutcome.TransientFailure)]
    [InlineData(PushSendOutcome.InvalidToken)]
    public async Task ExpiredOwner_MustNotChangeNewOwnerStateOrToken(PushSendOutcome late)
    {
        await using var f = await Fixture.Create();
        var sender = new Gates(late, PushSendOutcome.Accepted);
        await using var a = f.Db();
        var runA = Service(a, sender).DrainPendingAsync(default);
        await sender.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var e = f.Db())
        {
            var r = await e.PushOutbox.SingleAsync();
            Assert.NotNull(r.LeaseOwner);
            Assert.True(r.LeaseExpiresAt > DateTimeOffset.UtcNow);
            r.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await e.SaveChangesAsync();
        }

        await using var b = f.Db();
        var runB = Service(b, sender).DrainPendingAsync(default);
        await sender.Entered[1].Task.WaitAsync(TimeSpan.FromSeconds(10));
        sender.Release[1].SetResult();
        await runB;
        await using var before = f.Db();
        var rowB = await before.PushOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(PushOutboxStatuses.Accepted, rowB.Status);
        sender.Release[0].SetResult();
        await runA;
        await using var after = f.Db();
        var row = await after.PushOutbox.AsNoTracking().SingleAsync();
        var token = await after.Devices.Select(x => x.PushToken).SingleAsync();
        output.WriteLine(
            $"late={late}; status={row.Status}; attempts={row.AttemptCount}; token={token ?? "NULL"}; acceptedUnchanged={row.AcceptedAt == rowB.AcceptedAt}");
        Assert.Equal(rowB.Status, row.Status);
        Assert.Equal(rowB.UpdatedAt, row.UpdatedAt);
        Assert.Equal(rowB.AcceptedAt, row.AcceptedAt);
        Assert.Equal(2, row.AttemptCount);
        Assert.Null(row.LeaseOwner);
        Assert.Equal("synthetic-original", token);
    }

    [Fact]
    public async Task StalePendingSnapshot_MustNotBypassNewBackoff()
    {
        await using var f = await Fixture.Create();
        var sender = new ImmediateFailure();
        var gate = new ClaimGate();
        await using var stale = f.Db(gate);
        var staleDrain = Service(stale, sender).DrainPendingAsync(default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var winner = f.Db())
            await Service(winner, sender).DrainPendingAsync(default);
        await using (var check = f.Db())
        {
            var row = await check.PushOutbox.AsNoTracking().SingleAsync();
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal(PushOutboxStatuses.Failed, row.Status);
            Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);
            output.WriteLine($"winner finished: attempts={row.AttemptCount}; futureBackoff=true");
        }

        gate.Release.SetResult();
        await staleDrain;
        await using var final = f.Db();
        var end = await final.PushOutbox.AsNoTracking().SingleAsync();
        output.WriteLine($"stale resumed: sends={sender.Count}; attempts={end.AttemptCount}; status={end.Status}");
        Assert.Equal(1, sender.Count);
        Assert.Equal(1, end.AttemptCount);
        Assert.Equal(PushOutboxStatuses.Failed, end.Status);
        Assert.True(end.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    private static PushDispatchService Service(WakeMeshDbContext db, IPushSender sender)
    {
        var options = Options.Create(new FcmOptions { Configured = true });
        return new(
            db,
            sender,
            new NotificationStatusService(options, sender),
            options,
            NullLogger<PushDispatchService>.Instance);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection keepAlive;
        private readonly string cs;

        private Fixture(string cs)
        {
            this.cs = cs;
            keepAlive = new(cs);
        }

        public WakeMeshDbContext Db(DbCommandInterceptor? interceptor = null)
        {
            var b = new DbContextOptionsBuilder<WakeMeshDbContext>().UseSqlite(cs);
            if (interceptor is not null)
            {
                b.AddInterceptors(interceptor);
            }

            return new(b.Options);
        }

        public static async Task<Fixture> Create()
        {
            var f = new Fixture($"Data Source=t21-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            await f.keepAlive.OpenAsync();
            await using var db = f.Db();
            await db.Database.EnsureCreatedAsync();
            var id = Guid.NewGuid();
            db.Devices.Add(new DeviceEntity
            {
                Id = id,
                PushToken = "synthetic-original",
                Status = DeviceStatus.Active,
            });
            db.PushOutbox.Add(new PushOutboxEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = id,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
            return f;
        }

        public ValueTask DisposeAsync() => keepAlive.DisposeAsync();
    }

    /// <summary>
    /// Holds the first PushOutbox UPDATE (claim) so a second drain can win the generation first.
    /// Fixed order: enter gate before any competing claim proceeds.
    /// </summary>
    private sealed class ClaimGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int seen;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"PushOutbox\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref seen) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class Gates(params PushSendOutcome[] outcomes) : IPushSender
    {
        private int count;
        public bool IsAvailable => true;

        public TaskCompletionSource[] Entered { get; } =
            Enumerable.Range(0, outcomes.Length)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

        public TaskCompletionSource[] Release { get; } =
            Enumerable.Range(0, outcomes.Length)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

        public async Task<PushSendResult> SendSettingsChangedAsync(
            string token,
            Guid id,
            long rev,
            bool capable,
            CancellationToken ct)
        {
            var i = Interlocked.Increment(ref count) - 1;
            Entered[i].SetResult();
            await Release[i].Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new(outcomes[i]);
        }
    }

    private sealed class ImmediateFailure : IPushSender
    {
        public int Count;
        public bool IsAvailable => true;

        public Task<PushSendResult> SendSettingsChangedAsync(
            string token,
            Guid id,
            long rev,
            bool capable,
            CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(new PushSendResult(PushSendOutcome.TransientFailure, "synthetic"));
        }
    }
}
