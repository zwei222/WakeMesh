using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WakeMesh.Api.Data;

/// <summary>
/// Design-time factory for EF migrations. Credentials must come from the environment —
/// never embed passwords or production connection strings in source.
///
/// Required:
///   WAKEMESH_DESIGN_CONNECTION = connection string for the selected provider
/// Optional:
///   WAKEMESH_DESIGN_PROVIDER = SqlServer (default) | Sqlite | Postgres
/// </summary>
public sealed class WakeMeshDbContextFactory : IDesignTimeDbContextFactory<WakeMeshDbContext>
{
    public const string PostgresMigrationsAssemblyName = "WakeMesh.Api.PostgresMigrations";

    public WakeMeshDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("WAKEMESH_DESIGN_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "Set environment variable WAKEMESH_DESIGN_CONNECTION to a SQL connection string before running design-time EF commands. Do not embed credentials in source.");
        }

        var provider = Environment.GetEnvironmentVariable("WAKEMESH_DESIGN_PROVIDER") ?? "SqlServer";
        var builder = new DbContextOptionsBuilder<WakeMeshDbContext>();
        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            builder.UseSqlite(connection);
        }
        else if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            builder.UseSqlServer(connection);
        }
        else if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            // Same CA resolution as the published host: a relative Root Certificate is anchored on
            // AppContext.BaseDirectory, so `dotnet ef` and the running app agree on the file.
            builder.UseNpgsql(PostgresConfiguration.ResolveConnectionString(connection), PostgresConfiguration.Configure);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown WakeMesh design-time database provider '{provider}'. Supported: SqlServer, Sqlite, Postgres.");
        }

        return new WakeMeshDbContext(builder.Options);
    }
}
