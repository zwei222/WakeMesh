using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using WakeMesh.Api.Data;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WakeMesh.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"wakemesh-api-tests-{Guid.NewGuid():N}";

    public TestAuthFixture Auth { get; } = new();

    private Dictionary<string, string?> TestSettings => new()
    {
        // Shared in-memory SQLite so the factory host and all requests see one DB.
        ["ConnectionStrings:DefaultConnection"] = $"Data Source=file:{_dbName}?mode=memory&cache=shared",
        ["WakeMesh:Database:Provider"] = "Sqlite",
        // Issuer uses the public consumers-tenant GUID form (no personal tenant values).
        ["WakeMesh:Auth:Issuer"] = TestAuthFixture.Issuer,
        ["WakeMesh:Auth:Audience"] = TestAuthFixture.Audience,
        ["WakeMesh:Auth:RequiredScope"] = TestAuthFixture.RequiredScope,
        ["WakeMesh:Auth:AllowedAzp"] = TestAuthFixture.AllowedAzp,
        ["WakeMesh:Auth:AllowedOwnerOids:0"] = TestAuthFixture.AllowedOid,
        // HTTPS metadata URL satisfies production ValidateAuthConfig; no real network fetch (static fixture below).
        ["WakeMesh:Auth:MetadataAddress"] =
            $"https://login.microsoftonline.com/{TestAuthFixture.ConsumersTenantId}/v2.0/.well-known/openid-configuration",
        ["WakeMesh:Fcm:Configured"] = "false",
    };

    // Keep the in-memory DB alive for the factory lifetime.
    private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(TestSettings);
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        foreach (var kv in TestSettings)
        {
            builder.UseSetting(kv.Key, kv.Value);
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(TestSettings);
        });

        builder.ConfigureTestServices(services =>
        {
            // Only inject the test signing key + static OIDC metadata fixture.
            // Do NOT override ValidAlgorithms / ValidIssuer / ValidAudience — production
            // BuildTokenValidationParameters values (from TestSettings) must remain in force.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MetadataAddress = null!;
                options.ConfigurationManager = null;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = TestAuthFixture.Issuer,
                };
                options.TokenValidationParameters.IssuerSigningKey = Auth.SecurityKey;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            });
        });
    }

    public async Task InitializeAsync()
    {
        _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(TestSettings["ConnectionStrings:DefaultConnection"]);
        await _keepAlive.OpenAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
        }
    }
}
