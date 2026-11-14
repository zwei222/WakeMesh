using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace WakeMesh.Api.Data;

/// <summary>
/// Single source of truth for the PostgreSQL EF options shared by the runtime host and both
/// design-time factories. Keeping the migrations history table inside the private
/// <see cref="WakeMeshDbContext.PostgresSchema"/> schema is what stops the lineage from landing in
/// <c>public</c>, where Supabase Data API roles (anon/authenticated/service_role) would see it.
///
/// The options are applied only on the PostgreSQL path; SQL Server and SQLite keep their defaults.
/// </summary>
public static class PostgresConfiguration
{
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// Applies the migrations assembly and history-table location every PostgreSQL context must use.
    /// Callers must pass this to <c>UseNpgsql</c> so runtime and design time cannot drift.
    /// </summary>
    public static void Configure(NpgsqlDbContextOptionsBuilder options)
    {
        options.MigrationsAssembly(WakeMeshDbContextFactory.PostgresMigrationsAssemblyName);
        options.MigrationsHistoryTable(MigrationsHistoryTableName, WakeMeshDbContext.PostgresSchema);
    }
}
