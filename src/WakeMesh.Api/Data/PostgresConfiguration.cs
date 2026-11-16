using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    /// Where the build drops the CA when <c>WakeMeshDatabaseCaFile</c> is supplied, relative to the
    /// content root. This value is the one to put in <c>Root Certificate</c>; the host resolves it.
    /// </summary>
    public const string BundledCaRelativePath = "certs/supabase-ca.crt";

    /// <summary>
    /// Applies the migrations assembly and history-table location every PostgreSQL context must use.
    /// Callers must pass this to <c>UseNpgsql</c> so runtime and design time cannot drift.
    /// </summary>
    public static void Configure(NpgsqlDbContextOptionsBuilder options)
    {
        options.MigrationsAssembly(WakeMeshDbContextFactory.PostgresMigrationsAssemblyName);
        options.MigrationsHistoryTable(MigrationsHistoryTableName, WakeMeshDbContext.PostgresSchema);
    }

    /// <summary>
    /// Resolves the <c>Root Certificate</c> keyword of a PostgreSQL connection string to an absolute
    /// path rooted at <see cref="AppContext.BaseDirectory"/>.
    ///
    /// Deployments ship the CA next to the assembly (<see cref="BundledCaRelativePath"/>) and put a
    /// relative path in configuration, because the App Service content directory is not known at
    /// build time (and must not be hardcoded as <c>D:\home\site\wwwroot</c>). Resolving against the
    /// application base directory — never the process working directory — is what makes the same
    /// relative value work for the published host and for design-time EF runs.
    ///
    /// Rules, applied only to <c>Root Certificate</c>:
    /// - absent/blank: the caller's connection string is returned untouched. No CA is injected and
    ///   <c>SSL Mode</c> is never relaxed; "no CA configured" is not permission to stop verifying.
    /// - absolute: preserved verbatim.
    /// - relative: combined with <paramref name="baseDirectory"/> and normalised to a full path.
    /// - the resulting file must exist; otherwise this fails closed, because Npgsql would otherwise
    ///   surface a generic TLS failure at connect time.
    ///
    /// Every other keyword (SSL Mode, Password, Username, pooling, …) is carried through unchanged.
    /// Exceptions name the path only — never the connection string or its password.
    /// </summary>
    public static string ResolveConnectionString(string connectionString) =>
        ResolveConnectionString(connectionString, AppContext.BaseDirectory);

    /// <summary>
    /// Test seam for <see cref="ResolveConnectionString(string)"/>: lets a unit test pin the base
    /// directory instead of depending on the test host's own output directory. Production callers
    /// must use the public overload so the anchor stays <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    internal static string ResolveConnectionString(string connectionString, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var configured = builder.RootCertificate;
        if (string.IsNullOrEmpty(configured))
        {
            return connectionString;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            // Blank is a typo, not "use the system store": say so instead of letting Npgsql report it
            // later as an unexplained TLS failure.
            throw new InvalidOperationException(
                "PostgreSQL RootCertificate is set to a blank value. Supply the CA file path or remove the keyword.");
        }

        if (Path.IsPathRooted(configured))
        {
            if (!File.Exists(configured))
            {
                throw CaFileNotFound(configured);
            }

            // Already deployable as-is: do not rebuild (and thus re-quote) the caller's string.
            return connectionString;
        }

        var resolved = Path.GetFullPath(Path.Combine(baseDirectory, configured));
        if (!File.Exists(resolved))
        {
            throw CaFileNotFound(resolved);
        }

        builder.RootCertificate = resolved;
        return builder.ConnectionString;
    }

    private static InvalidOperationException CaFileNotFound(string resolvedPath) =>
        // Path only. Never echo the connection string, host, username, or password.
        new(
            $"PostgreSQL RootCertificate file not found at '{resolvedPath}'. Deploy the CA next to the " +
            $"application (for example '{BundledCaRelativePath}') or remove the RootCertificate keyword.");

    /// <summary>
    /// Fail-closed TLS precondition for the runtime host outside Development/Test. Pure: no
    /// environment access and no I/O; the caller decides whether the guard applies.
    ///
    /// Requires <b>both</b> <c>SSL Mode=VerifyFull</c> and a non-blank <c>Root Certificate</c>. A
    /// missing <c>SSL Mode</c> parses to <c>Prefer</c> and is rejected. On violation the thrown
    /// <see cref="InvalidOperationException"/> uses a fixed message that names the required keywords
    /// and never contains the connection string, host, username, or password.
    /// </summary>
    public static void EnsureVerifiedTls(string resolvedConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(resolvedConnectionString);
        if (builder.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(builder.RootCertificate))
        {
            throw new InvalidOperationException(
                "Production PostgreSQL requires SSL Mode=VerifyFull with an explicit Root Certificate. " +
                "Set SSL Mode=VerifyFull and configure the Root Certificate keyword; refusing to start " +
                "with weaker or unverified TLS.");
        }
    }
}
