using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.PostgresMigrations;

/// <summary>
/// Design-time factory for the PostgreSQL lineage. It is <b>required</b> in this assembly: EF only
/// scans the project and startup assemblies for a context/factory, and neither contains
/// <see cref="WakeMeshDbContext"/>, so without this factory
/// <c>dotnet ef migrations add -p/-s src/WakeMesh.Api.PostgresMigrations</c> fails with
/// "No DbContext was found".
///
/// It always selects PostgreSQL and ignores <c>WAKEMESH_DESIGN_PROVIDER</c>, so a SQL Server
/// migration can never be generated into this assembly. The SQL Server factory
/// (<see cref="WakeMeshDbContextFactory"/>) lives in the API assembly and is never scanned
/// together with this one, so the two do not collide.
///
/// <c>WAKEMESH_DESIGN_CONNECTION</c> is required; no connection is opened for
/// <c>migrations add</c>/<c>script</c>. Credentials must come from the environment, never source.
/// </summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<WakeMeshDbContext>
{
    public WakeMeshDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("WAKEMESH_DESIGN_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "Set environment variable WAKEMESH_DESIGN_CONNECTION to a PostgreSQL connection string before running PostgreSQL design-time EF commands. Do not embed credentials in source.");
        }

        var builder = new DbContextOptionsBuilder<WakeMeshDbContext>();
        builder.UseNpgsql(connection, PostgresConfiguration.Configure);
        return new WakeMeshDbContext(builder.Options);
    }
}
