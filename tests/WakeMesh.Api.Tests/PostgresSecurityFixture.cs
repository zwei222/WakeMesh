using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.Tests;

/// <summary>Exit code plus captured streams of one real psql invocation.</summary>
internal sealed record PsqlResult(int ExitCode, string StdOut, string StdErr)
{
    public override string ToString() => $"exit={ExitCode}";
}

/// <summary>
/// Cluster-level harness that mirrors a Supabase project as closely as a local disposable cluster can:
///
///  - a <b>non-superuser</b> <c>postgres</c> role (LOGIN CREATEDB CREATEROLE BYPASSRLS, member of
///    <c>pg_read_all_data</c>) then runs the real <c>deploy/postgres/00-bootstrap.sql</c>. A superuser
///    would hide the PG16+ "SET self-grant" pitfall and would bypass RLS, so it is never used for
///    bootstrap or for RLS assertions;
///  - Data API roles (<c>anon</c>/<c>authenticated</c>/<c>service_role</c> NOLOGIN plus an
///    <c>authenticator</c> LOGIN role) as Supabase manages them;
///  - one template database bootstrapped exactly once; every test database is cloned from it, so the
///    production schema owner/ACL/default-privileges are present without re-running the deliberately
///    non-idempotent bootstrap;
///  - the same SQL files the deployment uses, executed through psql so exit codes are direct.
/// </summary>
internal static class PostgresSecurityCluster
{
    public const string MigratorRole = "wakemesh_migrator";
    public const string ApiRole = "wakemesh_api";
    public const string TemplateDatabase = "wm_t47_template";
    public const string Schema = WakeMeshDbContext.PostgresSchema;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Cluster? _cluster;
    private static Task<Cluster>? _pending;

    public static async Task<Cluster> GetAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_cluster is not null)
            {
                return _cluster;
            }

            // Keep the task so a failure is not silently retried into a half-built cluster.
            _pending ??= BuildAsync();
            _cluster = await _pending;
            return _cluster;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Cluster> BuildAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("WAKEMESH_TEST_POSTGRES")
            ?? throw new InvalidOperationException("WAKEMESH_TEST_POSTGRES is required for the PostgreSQL security tests.");

        var builder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = "postgres" };
        var admin = builder.ConnectionString;
        var cluster = new Cluster(admin, builder.Host!, builder.Port, PostgresAssets.ResolvePsql());

        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await ExecAsync(conn,
                """
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'postgres') THEN
                    EXECUTE 'CREATE ROLE postgres LOGIN NOSUPERUSER CREATEDB CREATEROLE NOINHERIT NOREPLICATION BYPASSRLS';
                  END IF;
                END $$;
                """);
            // Mirrors the real project: postgres is not a superuser but reads all rows.
            await ExecAsync(conn, "GRANT pg_read_all_data TO postgres;");
            await ExecAsync(conn,
                """
                DO $$
                DECLARE r text;
                BEGIN
                  FOREACH r IN ARRAY ARRAY['anon', 'authenticated', 'service_role'] LOOP
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                      EXECUTE format('CREATE ROLE %I NOLOGIN NOINHERIT', r);
                    END IF;
                  END LOOP;
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticator') THEN
                    EXECUTE 'CREATE ROLE authenticator LOGIN NOINHERIT';
                  END IF;
                END $$;
                """);
            await ExecAsync(conn, "GRANT anon, authenticated, service_role TO authenticator;");
        }

        // Build the template with the real, non-idempotent bootstrap run as `postgres`.
        // The template is harness-owned: recreate it so a previous (possibly partial) run cannot leak in.
        await cluster.DropDatabaseAsync(TemplateDatabase);
        var created = await cluster.CreateDatabaseInternalAsync(TemplateDatabase, cloneTemplate: null);
        if (!created)
        {
            throw new InvalidOperationException($"Could not create the template database {TemplateDatabase}.");
        }

        cluster.BootstrapFirst = await cluster.PsqlFileAsync(TemplateDatabase, "postgres", "deploy/postgres/00-bootstrap.sql");
        cluster.BootstrapSecond = await cluster.PsqlFileAsync(TemplateDatabase, "postgres", "deploy/postgres/00-bootstrap.sql");

        // The template must also have the full migrated shape verified before any clone is trusted;
        // clones are verified individually by the tests after their own migration.
        return cluster;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public sealed class Cluster
    {
        private readonly string _adminConnection;
        private int _databaseSeq;

        internal Cluster(string adminConnection, string host, int port, string psqlPath)
        {
            _adminConnection = adminConnection;
            Host = host;
            Port = port;
            PsqlPath = psqlPath;
        }

        public string Host { get; }
        public int Port { get; }
        public string PsqlPath { get; }
        public PsqlResult? BootstrapFirst { get; internal set; }
        public PsqlResult? BootstrapSecond { get; internal set; }

        public async Task<PostgresTestDatabase> CreateDatabaseAsync(string prefix = "wm_t47")
        {
            var name = NextName(prefix);
            var ok = await CreateDatabaseInternalAsync(name, TemplateDatabase);
            if (!ok)
            {
                throw new InvalidOperationException($"Could not create test database {name}.");
            }

            return new PostgresTestDatabase(this, name, _adminConnection);
        }

        /// <summary>A database with no wakeMesh schema/roles applied (precheck / negative control).</summary>
        public async Task<PostgresTestDatabase> CreateUnbootstrappedDatabaseAsync(string prefix = "wm_t47_raw")
        {
            var name = NextName(prefix);
            var ok = await CreateDatabaseInternalAsync(name, cloneTemplate: null);
            if (!ok)
            {
                throw new InvalidOperationException($"Could not create test database {name}.");
            }

            return new PostgresTestDatabase(this, name, _adminConnection);
        }

        internal string NextName(string prefix)
        {
            var seq = Interlocked.Increment(ref _databaseSeq);
            var name = $"{prefix}_{seq}_{Guid.NewGuid():N}"[..(prefix.Length + 1 + 1 + 1 + 12)];
            if (!Regex.IsMatch(name, "^[a-z0-9_]+$"))
            {
                throw new InvalidOperationException($"Refusing to create database with unsafe name '{name}'.");
            }

            return name;
        }

        internal async Task<bool> CreateDatabaseInternalAsync(string name, string? cloneTemplate)
        {
            await using var conn = new NpgsqlConnection(_adminConnection);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = cloneTemplate is null
                ? $"CREATE DATABASE \"{name}\" OWNER \"postgres\""
                : $"CREATE DATABASE \"{name}\" TEMPLATE \"{cloneTemplate}\" OWNER \"postgres\"";
            await cmd.ExecuteNonQueryAsync();
            return true;
        }

        /// <summary>Runs a SQL file through psql as <paramref name="user"/>; returns the direct exit code.</summary>
        public Task<PsqlResult> PsqlFileAsync(string database, string user, string relativePath)
        {
            var path = PostgresAssets.RepoPath(relativePath);
            return RunPsqlAsync(new[] { "-f", path }, database, user);
        }

        public Task<PsqlResult> PsqlSqlAsync(string database, string user, string sql)
        {
            // -v ON_ERROR_STOP=1 makes a RAISE inside a DO block exit non-zero.
            return RunPsqlAsync(new[] { "-v", "ON_ERROR_STOP=1", "-c", sql }, database, user);
        }

        private async Task<PsqlResult> RunPsqlAsync(string[] commandArgs, string database, string user)
        {
            var psi = new ProcessStartInfo
            {
                FileName = PsqlPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            psi.ArgumentList.Add("-X");
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("-h");
            psi.ArgumentList.Add(Host);
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(Port.ToString());
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add(user);
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(database);
            foreach (var arg in commandArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored: the process may have exited between the check and the kill
                }

                throw new TimeoutException($"psql -U {user} -d {database} did not finish within 120s.");
            }

            return new PsqlResult(process.ExitCode, await stdout, await stderr);
        }

        public async Task DropDatabaseAsync(string name)
        {
            await using var conn = new NpgsqlConnection(_adminConnection);
            await conn.OpenAsync();
            await using (var terminate = conn.CreateCommand())
            {
                terminate.CommandText =
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();";
                terminate.Parameters.AddWithValue("db", name);
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{name}\"";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>One isolated database cloned from the bootstrapped template; dropped on dispose.</summary>
internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly PostgresSecurityCluster.Cluster _cluster;

    internal PostgresTestDatabase(PostgresSecurityCluster.Cluster cluster, string name, string adminConnection)
    {
        _cluster = cluster;
        DatabaseName = name;
        AdminConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
        AdminUser = new NpgsqlConnectionStringBuilder(adminConnection).Username!;
        MigratorConnectionString = Build(name, PostgresSecurityCluster.MigratorRole, cluster);
        ApiConnectionString = Build(name, PostgresSecurityCluster.ApiRole, cluster);
        AuthenticatorConnectionString = Build(name, "authenticator", cluster);
        ApiRoleConnectionString = ApiConnectionString;
    }

    public string DatabaseName { get; }
    public string AdminConnectionString { get; }
    public string AdminUser { get; }
    public string MigratorConnectionString { get; }
    public string ApiConnectionString { get; }
    public string ApiRoleConnectionString { get; }
    public string AuthenticatorConnectionString { get; }

    private static string Build(string database, string user, PostgresSecurityCluster.Cluster cluster)
        => new NpgsqlConnectionStringBuilder
        {
            Host = cluster.Host,
            Port = cluster.Port,
            Database = database,
            Username = user,
        }.ConnectionString;

    /// <summary>Runs the PostgreSQL migration lineage as wakemesh_migrator (the only role allowed).</summary>
    public async Task MigrateAsync()
    {
        await using var db = CreateMigratorDb();
        await db.Database.MigrateAsync();
    }

    public WakeMeshDbContext CreateMigratorDb() => CreateContext(MigratorConnectionString);

    public WakeMeshDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<WakeMeshDbContext>()
            .UseNpgsql(connectionString, PostgresConfiguration.Configure)
            .Options;
        return new WakeMeshDbContext(options);
    }

    /// <summary>Runtime-shaped context: connects as wakemesh_api, so RLS/grants apply.</summary>
    public WakeMeshDbContext CreateApiDb(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<WakeMeshDbContext>()
            .UseNpgsql(ApiConnectionString, PostgresConfiguration.Configure);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new WakeMeshDbContext(builder.Options);
    }

    public Task<PsqlResult> PsqlAsync(string user, string sql)
        => _cluster.PsqlSqlAsync(DatabaseName, user, sql);

    public Task<PsqlResult> PsqlFileAsync(string user, string relativePath)
        => _cluster.PsqlFileAsync(DatabaseName, user, relativePath);

    public async ValueTask DisposeAsync() => await _cluster.DropDatabaseAsync(DatabaseName);
}

/// <summary>Locates repository files and the psql binary without depending on the current directory.</summary>
internal static class PostgresAssets
{
    private static readonly Lazy<string> RepoRootLazy = new(FindRepoRoot);

    public static string RepoRoot => RepoRootLazy.Value;

    public static string RepoPath(string relativePath)
    {
        var path = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Expected repository file not found: {path}", path);
        }

        return path;
    }

    public static string ResolvePsql()
    {
        var pgBin = Environment.GetEnvironmentVariable("WAKEMESH_PG_BIN");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(pgBin))
        {
            candidates.Add(Path.Combine(pgBin, "psql"));
        }

        candidates.Add("/usr/lib/postgresql/17/bin/psql");
        candidates.Add("/usr/bin/psql");
        candidates.Add("/usr/local/bin/psql");
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "psql was not found; set WAKEMESH_PG_BIN to the PostgreSQL bin directory (the security tests execute deploy/postgres/*.sql through psql).");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WakeMesh.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root (WakeMesh.slnx) above {AppContext.BaseDirectory}.");
    }

    /// <summary>Appends one evidence row; used to save direct psql exit codes with the run.</summary>
    public static void AppendEvidence(string relativePath, string line)
    {
        try
        {
            var path = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Evidence capture must never fail the assertion it documents.
        }
    }
}
