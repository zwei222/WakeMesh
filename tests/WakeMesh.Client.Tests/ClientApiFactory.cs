using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WakeMesh.Api.Data;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WakeMesh.Client.Tests;

/// <summary>
/// Client-owned WebApplicationFactory. Does not modify API test fixtures.
/// Configures SQLite + real JwtBearer test signing key inside this test project only.
/// </summary>
public sealed class ClientApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"wakemesh-client-tests-{Guid.NewGuid():N}";

    public ClientTestAuth Auth { get; } = new();

    private Dictionary<string, string?> TestSettings => new()
    {
        ["ConnectionStrings:DefaultConnection"] = $"Data Source=file:{_dbName}?mode=memory&cache=shared",
        ["WakeMesh:Database:Provider"] = "Sqlite",
        ["WakeMesh:Auth:Issuer"] = ClientTestAuth.Issuer,
        ["WakeMesh:Auth:Audience"] = ClientTestAuth.Audience,
        ["WakeMesh:Auth:RequiredScope"] = ClientTestAuth.RequiredScope,
        ["WakeMesh:Auth:AllowedAzp"] = ClientTestAuth.AllowedAzp,
        ["WakeMesh:Auth:AllowedOwnerOids:0"] = ClientTestAuth.AllowedOid,
        ["WakeMesh:Auth:MetadataAddress"] =
            $"https://login.microsoftonline.com/{ClientTestAuth.ConsumersTenantId}/v2.0/.well-known/openid-configuration",
        ["WakeMesh:Fcm:Configured"] = "false",
    };

    private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(TestSettings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        foreach (var kv in TestSettings)
        {
            builder.UseSetting(kv.Key, kv.Value);
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(TestSettings));

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MetadataAddress = null!;
                options.ConfigurationManager = null;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = ClientTestAuth.Issuer,
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
