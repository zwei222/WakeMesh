using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Real disposable PostgreSQL regression (opt-in via WAKEMESH_TEST_POSTGRES).
/// Covers migrate lineage, column types, UTC round-trip, FK, RowVersion CAS,
/// outbox single-winner claim/complete, stale-owner reject, and rollback.
/// </summary>
public sealed class PostgresProviderTests
{
    private static string BaseConnection =>
        Environment.GetEnvironmentVariable("WAKEMESH_TEST_POSTGRES")
        ?? throw new InvalidOperationException("WAKEMESH_TEST_POSTGRES is required for this test class body.");

    [PostgresFact]
    public async Task Migrate_HasNoPendingModelChanges_And_ColumnTypes()
    {
        await using var fx = await PgFixture.CreateAsync();
        // Migration history is deploy metadata: only the migrator may read it.
        await using var db = fx.CreateMigratorDb();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.NotEmpty(applied);
        Assert.Contains(applied, id => id.Contains("InitialCreate", StringComparison.Ordinal));
        Assert.False(db.Database.HasPendingModelChanges());

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        // The fixture must exercise the runtime role, never a superuser or the object owner.
        await using (var whoami = conn.CreateCommand())
        {
            whoami.CommandText = "SELECT current_user, current_schema();";
            await using var reader = await whoami.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(PostgresSecurityCluster.ApiRole, reader.GetString(0));
        }

        // Schema comes from the model / live connection, never from a hardcoded 'public',
        // so a future schema move does not silently turn this check into a no-op.
        var schema = await ResolveSchemaAsync(db, typeof(DeviceEntity), conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.table_name, c.column_name, c.data_type, c.udt_name
            FROM information_schema.columns c
            WHERE c.table_schema = @schema
              AND (
                    (c.table_name = 'Devices' AND c.column_name IN ('RowVersion','CreatedAt'))
                 OR (c.table_name = 'AlarmGroups' AND c.column_name IN ('RowVersion'))
                 OR (c.table_name = 'PushOutbox' AND c.column_name IN ('CreatedAt','UpdatedAt','LeaseExpiresAt'))
              )
            ORDER BY c.table_name, c.column_name;
            """;
        cmd.Parameters.AddWithValue("schema", schema);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0) + "." + reader.GetString(1);
                rows[key] = reader.GetString(2) + "/" + reader.GetString(3);
            }
        }

        Assert.Contains("bigint", rows["Devices.RowVersion"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bigint", rows["AlarmGroups.RowVersion"], StringComparison.OrdinalIgnoreCase);
        Assert.True(
            rows["Devices.CreatedAt"].Contains("timestamp with time zone", StringComparison.OrdinalIgnoreCase)
            || rows["Devices.CreatedAt"].Contains("timestamptz", StringComparison.OrdinalIgnoreCase),
            $"Devices.CreatedAt type was {rows["Devices.CreatedAt"]}");
    }

    [PostgresFact]
    public async Task Crud_Utc_RoundTrip_And_Fk()
    {
        await using var fx = await PgFixture.CreateAsync();
        await using var db = fx.CreateDb();

        var stamp = DateTimeOffset.Parse("2026-11-14T12:34:56.789Z", CultureInfo.InvariantCulture);
        var deviceId = Guid.NewGuid();
        db.Devices.Add(new DeviceEntity
        {
            Id = deviceId,
            DisplayName = "pg-utc",
            CredentialHash = new byte[] { 1, 2, 3 },
            Status = DeviceStatus.Active,
            CreatedAt = stamp,
            RowVersion = 1,
        });
        db.AlarmGroups.Add(new AlarmGroupEntity
        {
            Id = "g-pg-1",
            Label = "pg",
            Repeat = "weekly",
            TimeZone = "Asia/Tokyo",
            RowVersion = 1,
            Targets =
            {
                new DeviceScheduleEntity
                {
                    Id = Guid.NewGuid(),
                    DeviceId = deviceId,
                    Time = "07:00",
                    SoundKey = "default",
                    ModifiedRevision = 1,
                }
            }
        });
        await db.SaveChangesAsync();

        await using var read = fx.CreateDb();
        var device = await read.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        Assert.Equal(TimeSpan.Zero, device.CreatedAt.Offset);
        Assert.Equal(stamp.UtcTicks, device.CreatedAt.UtcTicks);
        var schedule = await read.DeviceSchedules.AsNoTracking().SingleAsync(s => s.DeviceId == deviceId);
        Assert.Equal("g-pg-1", schedule.GroupId);
    }

    [PostgresFact]
    public async Task Device_RowVersion_Conflict_Has_Single_Winner()
    {
        await using var fx = await PgFixture.CreateAsync();
        Guid id;
        await using (var seed = fx.CreateDb())
        {
            id = Guid.NewGuid();
            seed.Devices.Add(new DeviceEntity
            {
                Id = id,
                DisplayName = "cas",
                CredentialHash = new byte[] { 9 },
                Status = DeviceStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                RowVersion = 1,
            });
            await seed.SaveChangesAsync();
        }

        await using var a = fx.CreateDb();
        await using var b = fx.CreateDb();
        var da = await a.Devices.SingleAsync(x => x.Id == id);
        var dbDevice = await b.Devices.SingleAsync(x => x.Id == id);
        da.DisplayName = "a-wins";
        da.RowVersion += 1;
        dbDevice.DisplayName = "b-loses";
        dbDevice.RowVersion += 1;
        await a.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());

        await using var check = fx.CreateDb();
        var final = await check.Devices.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("a-wins", final.DisplayName);
        Assert.Equal(2u, final.RowVersion);
    }

    // Outbox claim/lease/token oracles are covered through the real caller in
    // ProductionPostgresRegressionTests (concurrent drains, generation-matched claims,
    // expired-owner InvalidToken, snapshot token clear, rollback). The previous
    // hand-written claim UPDATE helper has been removed: it duplicated the production
    // predicate and only ever ran sequentially, so it could pass while the product
    // claim was broken.

    [PostgresFact]
    public async Task Transaction_Rollback_Discards_Partial_Writes()
    {
        await using var fx = await PgFixture.CreateAsync();
        var id = Guid.NewGuid();
        await using (var db = fx.CreateDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Devices.Add(new DeviceEntity
            {
                Id = id,
                DisplayName = "rolled-back",
                CredentialHash = new byte[] { 7 },
                Status = DeviceStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                RowVersion = 1,
            });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var check = fx.CreateDb();
        Assert.False(await check.Devices.AnyAsync(d => d.Id == id));
    }

    [PostgresFact]
    public async Task ApiFactory_Postgres_Path_Preserves_Auth_Contract_HealthAnonymous()
    {
        await using var fx = await PgFixture.CreateAsync();
        await using var api = ApiFactory.CreateFor(
            providerOverride: "Postgres",
            connectionStringOverride: fx.ConnectionString,
            // The fixture already migrated as wakemesh_migrator; the runtime role must not own DDL.
            skipDatabaseInitialize: true);
        var client = api.CreateClient();
        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);

        var denied = await client.GetAsync("/api/v1/devices");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);

        // Auth still works with real owner token (no auth bypass).
        using var okReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices");
        okReq.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", api.Auth.CreateAccessToken());
        var ok = await client.SendAsync(okReq);
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
    }

    [PostgresFact]
    public async Task ApiFactory_Postgres_Owner_Device_Crud_Roundtrip()
    {
        await using var fx = await PgFixture.CreateAsync();
        await using var api = ApiFactory.CreateFor(
            providerOverride: "Postgres",
            connectionStringOverride: fx.ConnectionString,
            skipDatabaseInitialize: true);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", api.Auth.CreateAccessToken());

        var created = await client.PostAsJsonAsync(
            "/api/v1/devices",
            new { displayName = "pg-http-crud" });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("credential").GetString()));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/devices");
        Assert.Contains(
            list.EnumerateArray(),
            d => d.GetProperty("id").GetGuid() == id
                 && d.GetProperty("displayName").GetString() == "pg-http-crud");

        // Durable write really landed in PostgreSQL (not just an in-memory response).
        await using var check = fx.CreateDb();
        var row = await check.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
        Assert.Equal("pg-http-crud", row.DisplayName);

        // Separate client: no Authorization header — the real guard must still return 401.
        using var anonymous = api.CreateClient();
        var deniedResp = await anonymous.GetAsync($"/api/v1/devices/{id}");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, deniedResp.StatusCode);
    }

    [PostgresFact]
    public async Task SqlServer_History_Ids_Are_Not_In_Postgres_Lineage()
    {
        await using var fx = await PgFixture.CreateAsync();
        await using var db = fx.CreateMigratorDb();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.DoesNotContain(applied, id => id == "20261113192122_InitialCreate");
        Assert.DoesNotContain(applied, id => id == "20261114000000_PushOutboxLeaseAndNotificationCapable");
        Assert.All(applied, id => Assert.DoesNotContain("PushOutboxLease", id, StringComparison.Ordinal));
    }

    private static async Task<string> ResolveSchemaAsync(
        WakeMeshDbContext db, Type entityType, NpgsqlConnection conn)
    {
        var modelSchema = db.Model.FindEntityType(entityType)?.GetSchema();
        if (!string.IsNullOrEmpty(modelSchema))
        {
            return modelSchema;
        }

        await using var current = conn.CreateCommand();
        current.CommandText = "SELECT current_schema();";
        return (string?)await current.ExecuteScalarAsync()
               ?? throw new InvalidOperationException("Could not resolve the current PostgreSQL schema.");
    }

    private sealed class PgFixture : IAsyncDisposable
    {
        private readonly PostgresTestDatabase _db;

        private PgFixture(PostgresTestDatabase db) => _db = db;

        /// <summary>Runtime-shaped connection string: connects as wakemesh_api, so RLS/grants apply.</summary>
        public string ConnectionString => _db.ApiConnectionString;

        public string DatabaseName => _db.DatabaseName;

        public static async Task<PgFixture> CreateAsync()
        {
            var cluster = await PostgresSecurityCluster.GetAsync();
            var db = await cluster.CreateDatabaseAsync("wm_t40");
            await db.MigrateAsync();
            return new PgFixture(db);
        }

        public WakeMeshDbContext CreateDb() => _db.CreateApiDb();

        /// <summary>
        /// Owner-shaped context for migration-metadata assertions. The runtime role must NOT be able
        /// to read <c>__EFMigrationsHistory</c> (asserted by PostgresSecurityTests).
        /// </summary>
        public WakeMeshDbContext CreateMigratorDb() => _db.CreateMigratorDb();

        public async ValueTask DisposeAsync() => await _db.DisposeAsync();
    }
}
