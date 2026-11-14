using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WakeMesh.Api.Data;

/// <summary>
/// Design-time factory for EF migrations. Credentials must come from the environment —
/// never embed passwords or production connection strings in source.
///
/// Required:
///   WAKEMESH_DESIGN_CONNECTION = SQL Server connection string
/// Optional:
///   WAKEMESH_DESIGN_PROVIDER = SqlServer (default) | Sqlite
/// </summary>
public sealed class WakeMeshDbContextFactory : IDesignTimeDbContextFactory<WakeMeshDbContext>
{
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
        else
        {
            builder.UseSqlServer(connection);
        }

        return new WakeMeshDbContext(builder.Options);
    }
}
