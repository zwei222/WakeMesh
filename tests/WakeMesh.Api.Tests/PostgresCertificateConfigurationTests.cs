using Npgsql;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Unit tests for the shared PostgreSQL root-certificate resolution used by the runtime host and
/// both design-time factories (<see cref="PostgresConfiguration"/>).
///
/// These tests never touch a database: they only pin the connection-string contract that decides
/// where Npgsql looks for the Supabase CA file. Every credential in this file is fictional.
///
/// Contract pinned here:
/// - a relative <c>Root Certificate</c> is resolved against the supplied base directory (the real
///   caller passes <c>AppContext.BaseDirectory</c>, never the process working directory),
/// - an absolute <c>Root Certificate</c> is preserved verbatim,
/// - an explicitly configured certificate that does not exist fails closed with a clear exception
///   that never contains the password or the connection string,
/// - omitting <c>Root Certificate</c> never adds a CA and never relaxes <c>SSL Mode</c>,
/// - every other keyword (SSL Mode, Password, Username, Host, pooling) survives unchanged.
/// </summary>
public sealed class PostgresCertificateConfigurationTests
{
    // Fictional values only. No production host, user, or password appears in this file.
    private const string FictionalPassword = "fictional-password-not-a-secret";

    private static string ConnectionString(string? rootCertificate, SslMode sslMode = SslMode.VerifyFull)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "pooler.invalid",
            Port = 5432,
            Username = "postgres.fictionalproject",
            Password = FictionalPassword,
            Database = "postgres",
            SslMode = sslMode,
            Pooling = true,
            MaxPoolSize = 10,
        };

        if (rootCertificate is not null)
        {
            builder.RootCertificate = rootCertificate;
        }

        return builder.ConnectionString;
    }

    [Fact]
    public void RelativeRootCertificate_IsResolvedAgainstSuppliedBaseDirectory()
    {
        using var sandbox = new CertificateSandbox();
        sandbox.WriteCertificate("certs/supabase-ca.crt");

        var resolved = PostgresConfiguration.ResolveConnectionString(
            ConnectionString("certs/supabase-ca.crt"),
            sandbox.BaseDirectory);

        var rootCertificate = new NpgsqlConnectionStringBuilder(resolved).RootCertificate;
        Assert.NotNull(rootCertificate);
        Assert.True(Path.IsPathRooted(rootCertificate));
        Assert.Equal(Path.Combine(sandbox.BaseDirectory, "certs", "supabase-ca.crt"), rootCertificate);
        Assert.True(File.Exists(rootCertificate));
    }

    [Fact]
    public void AbsoluteRootCertificate_IsPreservedVerbatim()
    {
        using var sandbox = new CertificateSandbox();
        var absolute = sandbox.WriteCertificate("elsewhere/supabase-ca.crt");

        var resolved = PostgresConfiguration.ResolveConnectionString(
            ConnectionString(absolute),
            sandbox.BaseDirectory);

        Assert.Equal(absolute, new NpgsqlConnectionStringBuilder(resolved).RootCertificate);
    }

    [Fact]
    public void MissingRelativeRootCertificate_FailsClosed()
    {
        using var sandbox = new CertificateSandbox();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresConfiguration.ResolveConnectionString(
                ConnectionString("certs/absent-ca.crt"),
                sandbox.BaseDirectory));

        Assert.Contains("RootCertificate", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FictionalPassword, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingAbsoluteRootCertificate_FailsClosed()
    {
        using var sandbox = new CertificateSandbox();
        var absolute = Path.Combine(sandbox.BaseDirectory, "nope", "absent-ca.crt");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresConfiguration.ResolveConnectionString(
                ConnectionString(absolute),
                sandbox.BaseDirectory));

        Assert.Contains("RootCertificate", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FictionalPassword, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutRootCertificate_AddsNoCaAndChangesNothing()
    {
        var original = ConnectionString(rootCertificate: null, sslMode: SslMode.Require);

        var resolved = PostgresConfiguration.ResolveConnectionString(original, Path.GetTempPath());

        // Untouched: no CA injected, no SSL Mode upgrade, no reformatting of secrets.
        Assert.Equal(original, resolved);

        var builder = new NpgsqlConnectionStringBuilder(resolved);
        Assert.True(string.IsNullOrEmpty(builder.RootCertificate));
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void BlankRootCertificate_FailsClosedInsteadOfFallingBack()
    {
        var original = ConnectionString("   ", SslMode.Prefer);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresConfiguration.ResolveConnectionString(original, Path.GetTempPath()));

        Assert.Contains("RootCertificate", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FictionalPassword, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvingCa_KeepsSslModeCredentialsAndPooling()
    {
        using var sandbox = new CertificateSandbox();
        sandbox.WriteCertificate("certs/supabase-ca.crt");
        var original = ConnectionString("certs/supabase-ca.crt");

        var builder = new NpgsqlConnectionStringBuilder(
            PostgresConfiguration.ResolveConnectionString(original, sandbox.BaseDirectory));

        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.Equal("pooler.invalid", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("postgres.fictionalproject", builder.Username);
        Assert.Equal(FictionalPassword, builder.Password);
        Assert.Equal("postgres", builder.Database);
        Assert.True(builder.Pooling);
        Assert.Equal(10, builder.MaxPoolSize);
        Assert.EndsWith(Path.Combine("certs", "supabase-ca.crt"), builder.RootCertificate, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvingCa_PreservesNonPooledSessionPosture()
    {
        // The deployer's session-pooler verification posture (explicit CA + Pooling=false) must reach
        // Npgsql exactly as written: the resolver adds the CA path and nothing else.
        using var sandbox = new CertificateSandbox();
        sandbox.WriteCertificate("certs/supabase-ca.crt");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "pooler.invalid",
            Port = 5432,
            Username = "postgres.fictionalproject",
            Password = FictionalPassword,
            Database = "postgres",
            SslMode = SslMode.VerifyFull,
            Pooling = false,
            RootCertificate = "certs/supabase-ca.crt",
        };

        var resolved = new NpgsqlConnectionStringBuilder(
            PostgresConfiguration.ResolveConnectionString(builder.ConnectionString, sandbox.BaseDirectory));

        Assert.False(resolved.Pooling);
        Assert.Equal(SslMode.VerifyFull, resolved.SslMode);
        Assert.Equal(FictionalPassword, resolved.Password);
        Assert.Equal(Path.Combine(sandbox.BaseDirectory, "certs", "supabase-ca.crt"), resolved.RootCertificate);
    }

    [Fact]
    public void RuntimeOverload_UsesApplicationBaseDirectory()
    {
        // The deployed layout is <content root>/certs/supabase-ca.crt next to the assembly, so the
        // public overload must anchor on AppContext.BaseDirectory and never on the process CWD.
        var original = ConnectionString("certs/does-not-exist-here.crt");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresConfiguration.ResolveConnectionString(original));

        Assert.Contains(
            Path.Combine(AppContext.BaseDirectory, "certs", "does-not-exist-here.crt"),
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed class CertificateSandbox : IDisposable
    {
        public CertificateSandbox()
        {
            BaseDirectory = Path.Combine(
                Path.GetTempPath(),
                "wakemesh-t53-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(BaseDirectory);
        }

        public string BaseDirectory { get; }

        public string WriteCertificate(string relativePath)
        {
            var fullPath = Path.Combine(BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "-----BEGIN CERTIFICATE-----\nfictional-test-fixture\n-----END CERTIFICATE-----\n");
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup of a temp sandbox.
            }
        }
    }
}
