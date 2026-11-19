using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Provider selection contracts: Postgres is explicit, unknown values fail closed,
/// design-time factory matches runtime MigrationsAssembly name.
/// </summary>
public sealed class DatabaseProviderSelectionTests
{
    private const string PostgresMigrationsAssembly = "WakeMesh.Api.PostgresMigrations";
    private const string DummySql =
        "Server=127.0.0.1,1433;Database=WakeMeshProviderSelectDummy;User Id=sa;Password=ProviderSelectDummy!;TrustServerCertificate=True;Encrypt=False";
    private const string DummyPg =
        "Host=127.0.0.1;Port=55432;Database=wakemesh_provider_select;Username=wakemesh;Password=ProviderSelectDummy!";
    private const string DummySqlite = "Data Source=:memory:";

    [Fact]
    public void DesignTimeFactory_UnknownProvider_FailsClosed()
    {
        using var _ = EnvScope.Set(
            ("WAKEMESH_DESIGN_CONNECTION", DummySql),
            ("WAKEMESH_DESIGN_PROVIDER", "Oracle"));

        var factory = new WakeMeshDbContextFactory();
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateDbContext(Array.Empty<string>()));
        Assert.Contains("provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesignTimeFactory_Postgres_UsesNpgsql_AndMigrationsAssembly()
    {
        using var _ = EnvScope.Set(
            ("WAKEMESH_DESIGN_CONNECTION", DummyPg),
            ("WAKEMESH_DESIGN_PROVIDER", "Postgres"));

        var factory = new WakeMeshDbContextFactory();
        using var db = factory.CreateDbContext(Array.Empty<string>());

        Assert.True(db.Database.IsNpgsql());

        // Relational options: MigrationsAssembly must match runtime constant.
        var assemblyName = GetMigrationsAssemblyName(db);
        Assert.Equal(PostgresMigrationsAssembly, assemblyName);
    }

    [Fact]
    public void DesignTimeFactory_Sqlite_And_SqlServer_RemainSupported()
    {
        using (EnvScope.Set(
                   ("WAKEMESH_DESIGN_CONNECTION", DummySqlite),
                   ("WAKEMESH_DESIGN_PROVIDER", "Sqlite")))
        {
            using var db = new WakeMeshDbContextFactory().CreateDbContext(Array.Empty<string>());
            Assert.True(db.Database.IsSqlite());
            // The PostgreSQL default schema must not leak into the SQLite test model.
            Assert.Null(db.Model.GetDefaultSchema());
            Assert.NotEqual(WakeMeshDbContext.PostgresSchema, GetRelationalOption(db, "MigrationsHistoryTableSchema"));
        }

        using (EnvScope.Set(
                   ("WAKEMESH_DESIGN_CONNECTION", DummySql),
                   ("WAKEMESH_DESIGN_PROVIDER", "SqlServer")))
        {
            using var db = new WakeMeshDbContextFactory().CreateDbContext(Array.Empty<string>());
            Assert.True(db.Database.IsSqlServer());
            // Frozen SQL Server lineage keeps the provider default schema/history table.
            Assert.Null(db.Model.GetDefaultSchema());
            Assert.NotEqual(WakeMeshDbContext.PostgresSchema, GetRelationalOption(db, "MigrationsHistoryTableSchema"));
        }
    }

    [Fact]
    public void HistoryTable_IsInThePrivateSchema_ForRuntimeAndBothDesignFactories()
    {
        // API design-time factory (SqlServer/Sqlite/Postgres selector).
        using (EnvScope.Set(
                   ("WAKEMESH_DESIGN_CONNECTION", DummyPg),
                   ("WAKEMESH_DESIGN_PROVIDER", "Postgres")))
        {
            using var db = new WakeMeshDbContextFactory().CreateDbContext(Array.Empty<string>());
            AssertPostgresHistory(db);
        }

        // PostgreSQL design-time factory: always PostgreSQL, regardless of the provider env var,
        // and it lives in its own assembly so it never collides with the API factory above.
        using (EnvScope.Set(
                   ("WAKEMESH_DESIGN_CONNECTION", DummyPg),
                   ("WAKEMESH_DESIGN_PROVIDER", "SqlServer")))
        {
            using var db = new WakeMesh.Api.PostgresMigrations.PostgresDesignTimeFactory()
                .CreateDbContext(Array.Empty<string>());
            Assert.True(db.Database.IsNpgsql());
            AssertPostgresHistory(db);
        }

        // Runtime host registration path.
        using var factory = ApiFactory.CreateFor(
            providerOverride: "Postgres",
            connectionStringOverride: DummyPg,
            skipDatabaseInitialize: true);
        using var scope = factory.Services.CreateScope();
        AssertPostgresHistory(scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>());
    }

    private static void AssertPostgresHistory(WakeMeshDbContext db)
    {
        Assert.True(db.Database.IsNpgsql());
        Assert.Equal(WakeMeshDbContext.PostgresSchema, db.Model.GetDefaultSchema());
        Assert.Equal("__EFMigrationsHistory", GetRelationalOption(db, "MigrationsHistoryTableName"));
        Assert.Equal(WakeMeshDbContext.PostgresSchema, GetRelationalOption(db, "MigrationsHistoryTableSchema"));
    }

    private static string? GetRelationalOption(WakeMeshDbContext db, string propertyName)
    {
        var opts = db.GetService<IDbContextOptions>();
        foreach (var extension in opts.Extensions)
        {
            var type = extension.GetType();
            while (type is not null)
            {
                var property = type.GetProperty(
                    propertyName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (property?.GetValue(extension) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                type = type.BaseType;
            }
        }

        return null;
    }

    [Fact]
    public void Model_Postgres_Maps_RowVersion_To_Long_Conversion_Only()
    {
        var options = new DbContextOptionsBuilder<WakeMeshDbContext>()
            .UseNpgsql(DummyPg)
            .Options;
        using var db = new WakeMeshDbContext(options);
        var deviceRv = db.Model.FindEntityType(typeof(DeviceEntity))!
            .FindProperty(nameof(DeviceEntity.RowVersion))!;
        var groupRv = db.Model.FindEntityType(typeof(AlarmGroupEntity))!
            .FindProperty(nameof(AlarmGroupEntity.RowVersion))!;

        Assert.Equal(typeof(uint), deviceRv.ClrType);
        Assert.Equal(typeof(long), deviceRv.GetProviderClrType());
        Assert.Equal(typeof(uint), groupRv.ClrType);
        Assert.Equal(typeof(long), groupRv.GetProviderClrType());
        // Must not be xmin system column mapping.
        Assert.False(
            string.Equals(deviceRv.GetColumnName(), "xmin", StringComparison.OrdinalIgnoreCase),
            "RowVersion must remain an application-managed column, not xmin.");
    }

    [Fact]
    public void Model_Sqlite_RowVersion_Stays_Uint_Without_Forced_Long_Provider()
    {
        var options = new DbContextOptionsBuilder<WakeMeshDbContext>()
            .UseSqlite(DummySqlite)
            .Options;
        using var db = new WakeMeshDbContext(options);
        var deviceRv = db.Model.FindEntityType(typeof(DeviceEntity))!
            .FindProperty(nameof(DeviceEntity.RowVersion))!;
        Assert.Equal(typeof(uint), deviceRv.ClrType);
        // Sqlite default provider type for uint is not forced through HasConversion<long>().
        Assert.NotEqual(typeof(long), deviceRv.GetProviderClrType());
    }

    [Fact]
    public async Task RuntimeHost_UnknownProvider_FailsClosed()
    {
        await using var factory = ApiFactory.CreateFor(providerOverride: "Cosmos");
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var scope = factory.Services.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        });
        // Host may wrap; require provider language somewhere in the chain.
        var messages = FlattenMessages(ex);
        Assert.Contains(messages, m => m.Contains("provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimeHost_Postgres_Config_Builds_Without_Auth_Bypass()
    {
        // Does not open a real PG socket: only DI registration path. Real CRUD is PostgresProviderTests.
        await using var factory = ApiFactory.CreateFor(
            providerOverride: "Postgres",
            connectionStringOverride: DummyPg,
            skipDatabaseInitialize: true);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        Assert.True(db.Database.IsNpgsql());
        Assert.Equal(PostgresMigrationsAssembly, GetMigrationsAssemblyName(db));
    }

    private static string? GetMigrationsAssemblyName(WakeMeshDbContext db)
    {
        var opts = db.GetService<IDbContextOptions>();
        foreach (var extension in opts.Extensions)
        {
            var prop = extension.GetType().GetProperty("MigrationsAssembly")
                       ?? extension.GetType().GetProperty("MigrationsAssemblyName");
            if (prop?.GetValue(extension) is string name && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            // RelationalOptionsExtension.MigrationsAssembly
            var type = extension.GetType();
            while (type is not null)
            {
                var p = type.GetProperty(
                    "MigrationsAssembly",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p?.GetValue(extension) is string n && !string.IsNullOrWhiteSpace(n))
                {
                    return n;
                }

                type = type.BaseType;
            }
        }

        return null;
    }

    private static IEnumerable<string> FlattenMessages(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            yield return cur.Message;
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                foreach (var m in FlattenMessages(inner))
                {
                    yield return m;
                }
            }
        }
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly List<(string Key, string? Prior)> _prior = new();

        public static EnvScope Set(params (string Key, string Value)[] pairs)
        {
            var scope = new EnvScope();
            foreach (var (key, value) in pairs)
            {
                scope._prior.Add((key, Environment.GetEnvironmentVariable(key)));
                Environment.SetEnvironmentVariable(key, value);
            }

            return scope;
        }

        public void Dispose()
        {
            foreach (var (key, prior) in _prior)
            {
                Environment.SetEnvironmentVariable(key, prior);
            }
        }
    }
}
