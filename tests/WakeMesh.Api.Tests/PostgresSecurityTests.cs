using Microsoft.EntityFrameworkCore;
using Npgsql;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Real-PostgreSQL security slice for the private `wakemesh` schema.
///
/// The cluster is shaped like a Supabase project (see <see cref="PostgresSecurityCluster"/>):
/// `postgres` is a non-superuser that ran the real bootstrap, `wakemesh_migrator` owns the schema
/// and ran the migration, and the runtime connects as `wakemesh_api`. A superuser is used only to
/// inject the defects that <c>90-verify.sql</c> must reject and to simulate "someone with admin
/// rights runs the expose recipe" -- never as evidence that RLS works.
///
/// Every check below is asserted against real psql exit codes and SQLSTATE-level errors, and the
/// direct exits are appended to the run's TSV evidence file.
/// </summary>
public sealed class PostgresSecurityTests
{
    private const string EvidencePath = ".evidence/deployment/t47-verify/verify-mutation-exits.tsv";

    [PostgresFact]
    public async Task Bootstrap_IsOneShot_AndFailsClosedOnRerun()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        var first = cluster.BootstrapFirst!;
        var second = cluster.BootstrapSecond!;

        Assert.Equal(0, first.ExitCode);
        Assert.NotEqual(0, second.ExitCode);
        // It must fail on the existing role, never silently adopt/drop it.
        Assert.Contains("already exists", second.StdErr, StringComparison.OrdinalIgnoreCase);

        PostgresAssets.AppendEvidence(
            EvidencePath,
            $"bootstrap\tpostgres\tfirst={first.ExitCode}\tsecond={second.ExitCode}\tsecondError=role already exists");
    }

    [PostgresFact]
    public async Task Verify_AcceptsTheMigratedBootstrapState()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var db = await cluster.CreateDatabaseAsync("wm_t47_ok");
        await db.MigrateAsync();

        var verify = await db.PsqlFileAsync("postgres", "deploy/postgres/90-verify.sql");

        Assert.True(
            verify.ExitCode == 0,
            $"90-verify.sql rejected a correct state: exit={verify.ExitCode}\nstderr:\n{verify.StdErr}\nstdout:\n{verify.StdOut}");
        PostgresAssets.AppendEvidence(EvidencePath, $"good-state\tpostgres\tverify={verify.ExitCode}\tcode=none");
    }

    [PostgresTheory]
    [InlineData("V3", "ALTER SCHEMA wakemesh OWNER TO postgres;", "admin", "ALTER SCHEMA wakemesh OWNER TO wakemesh_migrator;")]
    [InlineData("V1", "ALTER ROLE wakemesh_api BYPASSRLS;", "admin", "ALTER ROLE wakemesh_api NOBYPASSRLS;")]
    [InlineData("V2", "GRANT wakemesh_api TO wakemesh_migrator;", "admin", "REVOKE wakemesh_api FROM wakemesh_migrator;")]
    [InlineData("V5", "ALTER TABLE wakemesh.\"Devices\" DISABLE ROW LEVEL SECURITY;", "migrator", "ALTER TABLE wakemesh.\"Devices\" ENABLE ROW LEVEL SECURITY;")]
    [InlineData("V6", "GRANT SELECT ON TABLE wakemesh.\"__EFMigrationsHistory\" TO wakemesh_api;", "migrator", "REVOKE SELECT ON TABLE wakemesh.\"__EFMigrationsHistory\" FROM wakemesh_api;")]
    [InlineData("V7", "CREATE POLICY anon_read ON wakemesh.\"Devices\" FOR SELECT TO anon USING (true);", "migrator", "DROP POLICY anon_read ON wakemesh.\"Devices\";")]
    [InlineData("V4", "GRANT CREATE ON SCHEMA wakemesh TO wakemesh_api;", "migrator", "REVOKE CREATE ON SCHEMA wakemesh FROM wakemesh_api;")]
    [InlineData("V4", "GRANT USAGE ON SCHEMA wakemesh TO service_role;", "migrator", "REVOKE USAGE ON SCHEMA wakemesh FROM service_role;")]
    [InlineData("V8", "GRANT CREATE ON SCHEMA public TO wakemesh_api;", "admin", "REVOKE CREATE ON SCHEMA public FROM wakemesh_api;")]
    public async Task Verify_RejectsEveryDangerousMutation(string code, string mutation, string actedBy, string cleanup)
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var db = await cluster.CreateDatabaseAsync("wm_t47_bad");
        await db.MigrateAsync();

        var user = actedBy == "admin" ? db.AdminUser : PostgresSecurityCluster.MigratorRole;
        try
        {
            var applied = await db.PsqlAsync(user, mutation);
            Assert.True(applied.ExitCode == 0, $"mutation failed to apply as {user}: {applied.StdErr}");

            var verify = await db.PsqlFileAsync("postgres", "deploy/postgres/90-verify.sql");

            Assert.NotEqual(0, verify.ExitCode);
            Assert.Contains($"WakeMesh verify {code}", verify.StdErr, StringComparison.Ordinal);
            PostgresAssets.AppendEvidence(
                EvidencePath,
                $"{code}\t{actedBy}\tverify={verify.ExitCode}\tmatched={code}");
        }
        finally
        {
            // Role/schema changes are cluster-wide; restore them so test order cannot leak state.
            var restored = await db.PsqlAsync(
                actedBy == "admin" ? db.AdminUser : PostgresSecurityCluster.MigratorRole,
                cleanup);
            Assert.True(restored.ExitCode == 0, $"cleanup failed: {cleanup}: {restored.StdErr}");
        }
    }

    [PostgresFact]
    public async Task RuntimeRole_CanCrud_ButCannotDdlDumpOrTouchHistory()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var db = await cluster.CreateDatabaseAsync("wm_t47_api");
        await db.MigrateAsync();

        // CRUD through the runtime role really works (RLS policy + grants).
        var deviceId = Guid.NewGuid();
        await using (var api = db.CreateApiDb())
        {
            api.Devices.Add(new DeviceEntity
            {
                Id = deviceId,
                DisplayName = "api-crud",
                CredentialHash = new byte[] { 3, 3, 3 },
                Status = DeviceStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                RowVersion = 1,
            });
            await api.SaveChangesAsync();
        }

        await using (var api = db.CreateApiDb())
        {
            var read = await api.Devices.SingleAsync(d => d.Id == deviceId);
            Assert.Equal("api-crud", read.DisplayName);
            read.DisplayName = "api-updated";
            read.RowVersion += 1;
            await api.SaveChangesAsync();
        }

        await using (var api = db.CreateApiDb())
        {
            var read = await api.Devices.SingleAsync(d => d.Id == deviceId);
            Assert.Equal("api-updated", read.DisplayName);
            api.Devices.Remove(read);
            await api.SaveChangesAsync();
            Assert.False(await api.Devices.AnyAsync(d => d.Id == deviceId));
        }

        // Every DDL/history escape hatch must be denied by grants, not by client-side guards.
        var denials = new (string Sql, string Expected)[]
        {
            ("CREATE TABLE wakemesh.\"T47Evil\" (id int);", "permission denied for schema wakemesh"),
            ("ALTER TABLE wakemesh.\"Devices\" ADD COLUMN \"T47Evil\" int;", "must be owner of table"),
            ("DROP TABLE wakemesh.\"Devices\";", "must be owner of table"),
            ("ALTER TABLE wakemesh.\"Devices\" DISABLE ROW LEVEL SECURITY;", "must be owner of table"),
            ("TRUNCATE wakemesh.\"Devices\";", "permission denied for table"),
            ("SELECT count(*) FROM wakemesh.\"__EFMigrationsHistory\";", "permission denied for table"),
            ("CREATE ROLE wm_t47_evil;", "permission denied"),
        };

        foreach (var (sql, expected) in denials)
        {
            var result = await db.PsqlAsync(PostgresSecurityCluster.ApiRole, sql);
            Assert.True(
                result.ExitCode != 0,
                $"runtime role was NOT denied: {sql}\nexit={result.ExitCode}\nstdout={result.StdOut}");
            Assert.Contains(expected, result.StdErr, StringComparison.OrdinalIgnoreCase);
            PostgresAssets.AppendEvidence(
                EvidencePath,
                $"api-denial\tapi\texit={result.ExitCode}\t{expected}");
        }

        // The runtime role never owns anything in the schema.
        var ownership = await db.PsqlAsync(
            PostgresSecurityCluster.ApiRole,
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='wakemesh' AND c.relowner=(SELECT oid FROM pg_roles WHERE rolname=current_user);");
        Assert.Equal(0, ownership.ExitCode);
        Assert.Equal("0", ownership.StdOut.Trim());
    }

    [PostgresFact]
    public async Task DataApiRoles_CannotReachThePrivateSchema()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var db = await cluster.CreateDatabaseAsync("wm_t47_dataapi");
        await db.MigrateAsync();

        foreach (var role in new[] { "anon", "authenticated", "service_role" })
        {
            foreach (var target in new[] { "\"Devices\"", "\"__EFMigrationsHistory\"" })
            {
                var result = await db.PsqlAsync(
                    "authenticator",
                    $"SET ROLE {role}; SELECT count(*) FROM wakemesh.{target};");
                Assert.True(
                    result.ExitCode != 0,
                    $"{role} reached wakemesh.{target}: exit={result.ExitCode} stdout={result.StdOut}");
                Assert.Contains("permission denied for schema wakemesh", result.StdErr, StringComparison.OrdinalIgnoreCase);
                PostgresAssets.AppendEvidence(
                    EvidencePath,
                    $"data-api\t{role}\t{target.Trim('"')}\texit={result.ExitCode}\tpermission denied for schema wakemesh");
            }
        }
    }

    [PostgresFact]
    public async Task Misexposure_GrantsToAnon_StillReturnNoRows_AndInsertsViolateRls()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var db = await cluster.CreateDatabaseAsync("wm_t47_expose");
        await db.MigrateAsync();

        // One real row, written through the runtime role.
        await using (var api = db.CreateApiDb())
        {
            api.Devices.Add(new DeviceEntity
            {
                Id = Guid.NewGuid(),
                DisplayName = "secret-device",
                CredentialHash = new byte[] { 9, 9, 9 },
                Status = DeviceStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                RowVersion = 1,
            });
            await api.SaveChangesAsync();
        }

        // Simulate the documented "expose the schema" recipe run by the schema owner (migrator),
        // i.e. someone who legitimately may grant (USAGE + DML). This is the second barrier RLS
        // must hold: the first barrier (no grant at all) is covered by DataApiRoles_*.
        var expose = await db.PsqlAsync(
            PostgresSecurityCluster.MigratorRole,
            "GRANT USAGE ON SCHEMA wakemesh TO anon; GRANT SELECT, INSERT ON TABLE wakemesh.\"Devices\" TO anon;");
        Assert.True(expose.ExitCode == 0, expose.StdErr);

        var read = await db.PsqlAsync(
            "authenticator",
            "SET ROLE anon; SELECT count(*) FROM wakemesh.\"Devices\";");
        Assert.True(read.ExitCode == 0, $"anon read failed: {read.StdErr}");
        Assert.Equal("0", read.StdOut.Trim()); // RLS filters everything: no policy for anon

        var insert = await db.PsqlAsync(
            "authenticator",
            """
            SET ROLE anon;
            INSERT INTO wakemesh."Devices"
              ("Id","DisplayName","CredentialHash","Status","DesiredRevision","AppliedRevision","CreatedAt","CancelConfirmed","PushNotificationsCapable","RowVersion")
            VALUES (gen_random_uuid(),'intruder','\x00'::bytea,0,0,0,now(),false,false,1);
            """);
        Assert.NotEqual(0, insert.ExitCode);
        Assert.Contains("row-level security policy", insert.StdErr, StringComparison.OrdinalIgnoreCase);

        // ...and the verify script flags the misexposure grant itself (the schema ACL tripwire V4
        // fires before the per-table ACL check V6).
        var verify = await db.PsqlFileAsync("postgres", "deploy/postgres/90-verify.sql");
        Assert.NotEqual(0, verify.ExitCode);
        Assert.Contains("WakeMesh verify V4", verify.StdErr, StringComparison.Ordinal);

        PostgresAssets.AppendEvidence(
            EvidencePath,
            $"misexposure\towner-grant-then-anon\texit={verify.ExitCode}\t0rows=true\tinsertRlsViolation=true\tverifyMatched=V4");
    }

    [PostgresFact]
    public async Task RoleAttributes_PoliciesAndRlsFlags_MatchTheSpec()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();
        await using var db = await cluster.CreateDatabaseAsync("wm_t47_attrs");
        await db.MigrateAsync();

        await using var conn = new NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync();

        await using (var roles = conn.CreateCommand())
        {
            roles.CommandText = """
                SELECT rolname, rolcanlogin, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, rolreplication, rolinherit
                FROM pg_roles
                WHERE rolname IN ('wakemesh_migrator','wakemesh_api')
                ORDER BY rolname;
                """;
            await using var reader = await roles.ExecuteReaderAsync();
            var seen = new List<string>();
            while (await reader.ReadAsync())
            {
                seen.Add(reader.GetString(0));
                Assert.True(reader.GetBoolean(1), $"{reader.GetString(0)} must be LOGIN");
                Assert.False(reader.GetBoolean(2), $"{reader.GetString(0)} must not be SUPERUSER");
                Assert.False(reader.GetBoolean(3), $"{reader.GetString(0)} must not be BYPASSRLS");
                Assert.False(reader.GetBoolean(4), $"{reader.GetString(0)} must not be CREATEROLE");
                Assert.False(reader.GetBoolean(5), $"{reader.GetString(0)} must not be CREATEDB");
                Assert.False(reader.GetBoolean(6), $"{reader.GetString(0)} must not be REPLICATION");
                Assert.False(reader.GetBoolean(7), $"{reader.GetString(0)} must be NOINHERIT");
            }

            Assert.Equal(new[] { "wakemesh_api", "wakemesh_migrator" }, seen);
        }

        await using (var tables = conn.CreateCommand())
        {
            tables.CommandText = """
                SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                       (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid),
                       pg_get_userbyid(c.relowner)
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'wakemesh' AND c.relkind = 'r'
                ORDER BY c.relname;
                """;
            await using var reader = await tables.ExecuteReaderAsync();
            var business = 0;
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                Assert.True(reader.GetBoolean(1), $"{name}: RLS must be enabled");
                Assert.False(reader.GetBoolean(2), $"{name}: RLS must not be FORCEd");
                Assert.Equal("wakemesh_migrator", reader.GetString(4));
                if (name == "__EFMigrationsHistory")
                {
                    Assert.Equal(0L, reader.GetInt64(3));
                }
                else
                {
                    Assert.Equal(1L, reader.GetInt64(3));
                    business++;
                }
            }

            Assert.Equal(7, business);
        }
    }

    [PostgresFact]
    public async Task Migration_PrecheckFailsClosed_WithoutBootstrapOrAsNonMigrator()
    {
        var cluster = await PostgresSecurityCluster.GetAsync();

        // 1. No bootstrapped schema in this database -> the migrator has no CREATE on the database.
        await using (var raw = await cluster.CreateUnbootstrappedDatabaseAsync("wm_t47_raw"))
        {
            await using var db = raw.CreateMigratorDb();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.MigrateAsync());
            Assert.Contains("permission denied", Flatten(ex).ToLowerInvariant());
        }

        // 2. A superuser/admin connection must be refused before any DDL runs.
        await using (var bootstrapped = await cluster.CreateDatabaseAsync("wm_t47_wrongrole"))
        {
            await using var db = bootstrapped.CreateContext(bootstrapped.AdminConnectionString);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.MigrateAsync());
            Assert.Contains("must run as wakemesh_migrator", Flatten(ex));
        }
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            messages.Add(cur.Message);
        }

        if (ex is AggregateException agg)
        {
            messages.AddRange(agg.InnerExceptions.SelectMany(e => new[] { e.Message }));
        }

        return string.Join("\n", messages);
    }
}
