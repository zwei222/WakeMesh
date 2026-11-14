using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Real-PostgreSQL regression for the push outbox drain, driven only through the production
/// caller (<see cref="PushDispatchService.DrainPendingAsync"/>) and a real FCM-shaped sender.
///
/// Deliberately avoids the weaker patterns that can pass without exercising the product:
/// - no hand-written claim/settle SQL (the old helper duplicated the predicate and never ran it),
/// - no "call drain A to completion, then drain B" winner assertion,
/// - no sleeps: interleavings are fixed with a barrier or a TCS gate inside the command pipeline.
///
/// Each test creates and drops its own database, so rows (and any backoff a failed attempt would
/// schedule) never leak between cases. Table/schema names are resolved from DbContext metadata or
/// the live connection's current schema, never from a hardcoded 'public'.
///
/// Opt-in via WAKEMESH_TEST_POSTGRES (scripts/pg-test-local.sh env|run).
/// </summary>
public sealed class ProductionPostgresRegressionTests
{
    // ---------------------------------------------------------------------
    // 1. Two drains race; the generation-matched claim must let exactly one
    //    owner per row (and increment AttemptCount once).
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task ConcurrentDrains_ClaimEachRowExactlyOnce()
    {
        await using var pg = await PgDb.CreateAsync();
        await using (var seed = pg.Db())
        {
            var baseTime = DateTimeOffset.UtcNow.AddMinutes(-3);
            for (var i = 0; i < 2; i++)
            {
                SeedDueOutbox(seed, $"concurrent-token-{i}", baseTime.AddSeconds(i));
            }

            await seed.SaveChangesAsync();
        }

        var sender = new CountingSender(PushSendOutcome.Accepted);
        var barrier = new Barrier(2);

        await using var a = pg.Db(new FirstClaimBarrier(barrier));
        await using var b = pg.Db(new FirstClaimBarrier(barrier));

        var drainA = Task.Run(() => Service(a, sender).DrainPendingAsync(default));
        var drainB = Task.Run(() => Service(b, sender).DrainPendingAsync(default));
        var summaries = await Task.WhenAll(drainA, drainB).WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = pg.Db();
        var rows = await check.PushOutbox.AsNoTracking().OrderBy(o => o.CreatedAt).ToListAsync();
        Assert.Equal(2, rows.Count);

        // Exactly one send per row/device, one claim per row.
        Assert.All(rows, row =>
        {
            Assert.Equal(PushOutboxStatuses.Accepted, row.Status);
            Assert.Equal(1, row.AttemptCount);
            Assert.Null(row.LeaseOwner);
            Assert.Null(row.LeaseExpiresAt);
        });
        Assert.Equal(2, sender.Total);
        Assert.All(sender.SendsByDevice.Values, count => Assert.Equal(1, count));
        Assert.Equal(2, rows.Select(r => r.DeviceId).Distinct().Count());

        // Both drains together observed exactly two successful claims.
        Assert.Equal(2, summaries.Sum(s => s.Attempted));
        Assert.Equal(2, summaries.Sum(s => s.Accepted));
    }

    // ---------------------------------------------------------------------
    // 2. Nullable snapshot equality: a drain whose candidate snapshot was
    //    taken before a competing drain completed must not re-send (its claim
    //    compares Status/AttemptCount/NextAttemptAt/LeaseOwner/LeaseExpiresAt,
    //    including NULLs).
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task StaleSnapshot_DrainCannotSendAfterCompetingCompletion()
    {
        await using var pg = await PgDb.CreateAsync();
        Guid outboxId;
        Guid deviceId;
        await using (var seed = pg.Db())
        {
            (deviceId, outboxId) = SeedDueOutbox(seed, "stale-token", DateTimeOffset.UtcNow.AddMinutes(-3));
            await seed.SaveChangesAsync();
        }

        var sender = new CountingSender(PushSendOutcome.Accepted);
        var gate = new HoldFirstClaim();

        await using var stale = pg.Db(gate);
        var staleDrain = Task.Run(() => Service(stale, sender).DrainPendingAsync(default));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Competing drain completes while the stale claim is held before execution.
        await using (var winner = pg.Db())
        {
            var winnerSummary = await Service(winner, sender).DrainPendingAsync(default);
            Assert.Equal(1, winnerSummary.Attempted);
            Assert.Equal(1, winnerSummary.Accepted);
        }

        gate.Release.TrySetResult();
        var staleSummary = await staleDrain.WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = pg.Db();
        var row = await check.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == outboxId);
        var device = await check.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);

        // Production claim re-reads and matches the full observed generation:
        // the stale drain sees a changed generation, claims 0 rows, and sends nothing.
        Assert.Equal(0, staleSummary.Attempted);
        Assert.Equal(1, sender.Total);
        Assert.Equal(PushOutboxStatuses.Accepted, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.LeaseOwner);
        Assert.Equal("stale-token", device.PushToken);
    }

    // ---------------------------------------------------------------------
    // 2b. Same protection for the failure path: a stale snapshot must not
    //     reclaim past the backoff a winner just scheduled.
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task StaleSnapshot_CannotBypassNewBackoff()
    {
        await using var pg = await PgDb.CreateAsync();
        Guid outboxId;
        await using (var seed = pg.Db())
        {
            (_, outboxId) = SeedDueOutbox(seed, "backoff-token", DateTimeOffset.UtcNow.AddMinutes(-3));
            await seed.SaveChangesAsync();
        }

        var sender = new CountingSender(PushSendOutcome.TransientFailure);
        var gate = new HoldFirstClaim();

        await using var stale = pg.Db(gate);
        var staleDrain = Task.Run(() => Service(stale, sender).DrainPendingAsync(default));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await using (var winner = pg.Db())
        {
            var winnerSummary = await Service(winner, sender).DrainPendingAsync(default);
            Assert.Equal(1, winnerSummary.Attempted);
            Assert.Equal(1, winnerSummary.Failed);
        }

        gate.Release.TrySetResult();
        var staleSummary = await staleDrain.WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = pg.Db();
        var row = await check.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == outboxId);

        Assert.Equal(0, staleSummary.Attempted);
        Assert.Equal(1, sender.Total);              // no second send past the backoff
        Assert.Equal(PushOutboxStatuses.Failed, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.NextAttemptAt);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Null(row.LeaseOwner);
    }

    // ---------------------------------------------------------------------
    // 3. Expired lease + late InvalidToken: the late owner must not clear the
    //    token or overwrite the newer owner's terminal state. AttemptCount is
    //    incremented atomically per successful claim (1 -> 2).
    //    (T21 CAS oracle, exercised through the production drain on PostgreSQL.)
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task ExpiredOwner_LateInvalidToken_CannotClearTokenOrOverwriteNewOwner()
    {
        await using var pg = await PgDb.CreateAsync();
        Guid outboxId;
        Guid deviceId;
        await using (var seed = pg.Db())
        {
            (deviceId, outboxId) = SeedDueOutbox(seed, "synthetic-original", DateTimeOffset.UtcNow.AddMinutes(-3));
            await seed.SaveChangesAsync();
        }

        var sender = new GatedSender(PushSendOutcome.InvalidToken, PushSendOutcome.Accepted);

        await using var first = pg.Db();
        var firstDrain = Task.Run(() => Service(first, sender).DrainPendingAsync(default));
        await sender.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Only the DB lease is expired; no clock or sleep is involved.
        await using (var expire = pg.Db())
        {
            var claimed = await expire.PushOutbox.SingleAsync(o => o.Id == outboxId);
            Assert.Equal(PushOutboxStatuses.Sending, claimed.Status);
            Assert.NotNull(claimed.LeaseOwner);
            claimed.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await expire.SaveChangesAsync();
        }

        await using var second = pg.Db();
        var secondDrain = Task.Run(() => Service(second, sender).DrainPendingAsync(default));
        await sender.Entered[1].Task.WaitAsync(TimeSpan.FromSeconds(20));
        sender.Release[1].TrySetResult();
        var secondSummary = await secondDrain.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, secondSummary.Attempted);
        Assert.Equal(1, secondSummary.Accepted);

        // Late owner resumes with InvalidToken.
        sender.Release[0].TrySetResult();
        var firstSummary = await firstDrain.WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = pg.Db();
        var row = await check.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == outboxId);
        var device = await check.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);

        Assert.Equal(0, firstSummary.InvalidToken);
        Assert.Equal(0, firstSummary.Discarded);
        Assert.Equal(PushOutboxStatuses.Accepted, row.Status);
        Assert.Equal(2, row.AttemptCount);          // one increment per successful claim
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);
        Assert.Equal("synthetic-original", device.PushToken); // not cleared by the late owner
    }

    // ---------------------------------------------------------------------
    // 4. InvalidToken token clear is snapshot-matched: a token rotated while
    //    the send is in flight must survive, while the outbox row still
    //    completes (both writes share one transaction).
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task InvalidToken_ClearsOnlyTheSnapshotToken()
    {
        await using var pg = await PgDb.CreateAsync();
        Guid outboxId;
        Guid deviceId;
        await using (var seed = pg.Db())
        {
            (deviceId, outboxId) = SeedDueOutbox(seed, "token-before-send", DateTimeOffset.UtcNow.AddMinutes(-3));
            await seed.SaveChangesAsync();
        }

        var sender = new GatedSender(PushSendOutcome.InvalidToken);
        await using var drainDb = pg.Db();
        var drain = Task.Run(() => Service(drainDb, sender).DrainPendingAsync(default));
        await sender.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Token rotates while the invalid-token send is in flight.
        DateTimeOffset rotationStamp;
        await using (var rotate = pg.Db())
        {
            var device = await rotate.Devices.SingleAsync(d => d.Id == deviceId);
            rotationStamp = DateTimeOffset.UtcNow;
            device.PushToken = "token-rotated";
            device.PushTokenUpdatedAt = rotationStamp;
            await rotate.SaveChangesAsync();
        }

        sender.Release[0].TrySetResult();
        var summary = await drain.WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = pg.Db();
        var row = await check.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == outboxId);
        var finalDevice = await check.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        var tokenUpdatedAt = finalDevice.PushTokenUpdatedAt;

        Assert.Equal(1, summary.InvalidToken);
        Assert.Equal(1, summary.Discarded);
        Assert.Equal(PushOutboxStatuses.Discarded, row.Status);
        Assert.Equal("token-rotated", finalDevice.PushToken);   // conditional clear matched 0 rows
        Assert.NotNull(row.LastErrorCode);
        Assert.Null(row.LeaseOwner);
        Assert.NotNull(tokenUpdatedAt);                          // untouched by the failed clear
        Assert.Equal(rotationStamp.ToUnixTimeMilliseconds(), tokenUpdatedAt!.Value.ToUnixTimeMilliseconds());
    }

    // ---------------------------------------------------------------------
    // 5. InvalidToken completion + token clear are one transaction: a failure
    //    on the token write rolls the outbox completion back. Nothing is
    //    claimed as discarded and the token is untouched.
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task InvalidToken_TransactionRollsBackOutboxCompletion()
    {
        await using var pg = await PgDb.CreateAsync();
        Guid outboxId;
        Guid deviceId;
        await using (var seed = pg.Db())
        {
            (deviceId, outboxId) = SeedDueOutbox(seed, "rollback-token", DateTimeOffset.UtcNow.AddMinutes(-3));
            await seed.SaveChangesAsync();
        }

        var sender = new CountingSender(PushSendOutcome.InvalidToken);

        await using var drainDb = pg.Db(new ThrowOnDeviceUpdate());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(drainDb, sender).DrainPendingAsync(default));

        await using var check = pg.Db();
        var row = await check.PushOutbox.AsNoTracking().SingleAsync(o => o.Id == outboxId);
        var device = await check.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);

        // Outbox CAS ran first, then the token write failed: the whole unit rolled back.
        Assert.Equal(PushOutboxStatuses.Sending, row.Status);
        Assert.NotNull(row.LeaseOwner);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.LastErrorCode);
        Assert.Equal("rollback-token", device.PushToken);
        Assert.Equal(1, sender.Total);
    }

    // ---------------------------------------------------------------------
    // 6. Schema lookup must come from the model/connection, not a hardcoded
    //    'public', so a future schema move does not silently empty the checks.
    // ---------------------------------------------------------------------
    [PostgresFact]
    public async Task SchemaAssertions_FollowModelMetadataAndCurrentSchema()
    {
        await using var pg = await PgDb.CreateAsync();
        await using var db = pg.Db();
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();

        var modelSchema = db.Model.FindEntityType(typeof(PushOutboxEntity))?.GetSchema();
        string schema;
        if (!string.IsNullOrEmpty(modelSchema))
        {
            schema = modelSchema;
        }
        else
        {
            await using var current = conn.CreateCommand();
            current.CommandText = "SELECT current_schema();";
            schema = (string?)await current.ExecuteScalarAsync() ?? "public";
        }

        Assert.False(string.IsNullOrWhiteSpace(schema));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND table_name = 'PushOutbox'
              AND column_name IN ('LeaseOwner', 'LeaseExpiresAt', 'AttemptCount', 'NextAttemptAt');
            """;
        cmd.Parameters.AddWithValue("schema", schema);
        Assert.Equal(4L, (long)(await cmd.ExecuteScalarAsync())!);

        await using var devices = conn.CreateCommand();
        devices.CommandText = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = 'Devices' AND column_name = 'RowVersion';
            """;
        devices.Parameters.AddWithValue("schema", schema);
        var rowVersionType = (string?)await devices.ExecuteScalarAsync();
        Assert.Equal("bigint", rowVersionType);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------
    private static PushDispatchService Service(WakeMeshDbContext db, IPushSender sender)
    {
        var options = Options.Create(new FcmOptions { Configured = true });
        return new PushDispatchService(
            db,
            sender,
            new NotificationStatusService(options, sender),
            options,
            NullLogger<PushDispatchService>.Instance);
    }

    private static (Guid DeviceId, Guid OutboxId) SeedDueOutbox(
        WakeMeshDbContext db,
        string token,
        DateTimeOffset createdAt)
    {
        var deviceId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        db.Devices.Add(new DeviceEntity
        {
            Id = deviceId,
            DisplayName = "production-caller",
            CredentialHash = new byte[] { 1, 2, 3 },
            Status = DeviceStatus.Active,
            CreatedAt = createdAt,
            PushToken = token,
            PushNotificationsCapable = true,
            RowVersion = 1,
        });
        db.PushOutbox.Add(new PushOutboxEntity
        {
            Id = outboxId,
            DeviceId = deviceId,
            DesiredRevision = 1,
            MessageType = PushMessageTypes.SettingsChanged,
            Status = PushOutboxStatuses.Pending,
            AttemptCount = 0,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            NextAttemptAt = createdAt,
        });
        return (deviceId, outboxId);
    }

    private static bool IsOutboxUpdate(DbCommand command) =>
        IsCommandOnTable(command, "UPDATE", "PushOutbox");

    /// <summary>
    /// Matches <c>UPDATE [schema.]"Table"</c>. The PostgreSQL default schema makes EF emit
    /// <c>UPDATE wakemesh."PushOutbox"</c>, so a bare-name prefix check would silently stop firing.
    /// </summary>
    private static bool IsCommandOnTable(DbCommand command, string verb, string table)
        => System.Text.RegularExpressions.Regex.IsMatch(
            command.CommandText,
            $@"^\s*{verb}\s+(?:""?[A-Za-z_][A-Za-z0-9_]*""?\.)?""{System.Text.RegularExpressions.Regex.Escape(table)}""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Holds each context's first outbox UPDATE until both drains arrive (deterministic race).</summary>
    private sealed class FirstClaimBarrier(Barrier barrier) : DbCommandInterceptor
    {
        private int _seen;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsOutboxUpdate(command) && Interlocked.Increment(ref _seen) == 1)
            {
                await Task.Run(
                    () =>
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                        {
                            throw new TimeoutException("concurrent claim barrier timed out");
                        }
                    },
                    cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Pauses the first outbox UPDATE before it executes, so a competitor can finish first.</summary>
    private sealed class HoldFirstClaim : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsOutboxUpdate(command) && Interlocked.Increment(ref _seen) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Fails the in-transaction device token write to prove the completion rolls back.</summary>
    private sealed class ThrowOnDeviceUpdate : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsCommandOnTable(command, "UPDATE", "Devices"))
            {
                throw new InvalidOperationException("injected device update failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingSender(params PushSendOutcome[] outcomes) : IPushSender
    {
        private int _count;
        public ConcurrentDictionary<Guid, int> SendsByDevice { get; } = new();

        public int Total => _count;

        public bool IsAvailable => true;

        public Task<PushSendResult> SendSettingsChangedAsync(
            string token, Guid deviceId, long revision, bool notificationCapable, CancellationToken ct)
        {
            SendsByDevice.AddOrUpdate(deviceId, 1, (_, current) => current + 1);
            var index = Interlocked.Increment(ref _count) - 1;
            var outcome = outcomes.Length == 0
                ? PushSendOutcome.Accepted
                : outcomes[Math.Min(index, outcomes.Length - 1)];
            return Task.FromResult(new PushSendResult(outcome, outcome == PushSendOutcome.InvalidToken ? "unregistered" : null));
        }
    }

    private sealed class GatedSender(params PushSendOutcome[] outcomes) : IPushSender
    {
        private int _count;

        public TaskCompletionSource[] Entered { get; } =
            outcomes.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        public TaskCompletionSource[] Release { get; } =
            outcomes.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        public bool IsAvailable => true;

        public async Task<PushSendResult> SendSettingsChangedAsync(
            string token, Guid deviceId, long revision, bool notificationCapable, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _count) - 1;
            Entered[index].TrySetResult();
            await Release[index].Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var outcome = outcomes[index];
            return new PushSendResult(outcome, outcome == PushSendOutcome.InvalidToken ? "unregistered" : null);
        }
    }

    /// <summary>One isolated database per test; bootstrapped schema, migrated as migrator, dropped after.</summary>
    private sealed class PgDb : IAsyncDisposable
    {
        private readonly PostgresTestDatabase _db;

        private PgDb(PostgresTestDatabase db) => _db = db;

        public string DatabaseName => _db.DatabaseName;

        /// <summary>Runtime-shaped connection string (wakemesh_api): RLS and grants apply here.</summary>
        public string ConnectionString => _db.ApiConnectionString;

        public static async Task<PgDb> CreateAsync()
        {
            var cluster = await PostgresSecurityCluster.GetAsync();
            var db = await cluster.CreateDatabaseAsync("wm_t42");
            await db.MigrateAsync();
            return new PgDb(db);
        }

        public WakeMeshDbContext Db(params IInterceptor[] interceptors)
            => _db.CreateApiDb(interceptors);

        public async ValueTask DisposeAsync() => await _db.DisposeAsync();
    }
}
