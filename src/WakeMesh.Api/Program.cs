using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WakeMesh.Api.Auth;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Never enable auth debug dumps of tokens by default.
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.IdentityModel", LogLevel.Warning);

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<FcmOptions>(builder.Configuration.GetSection(FcmOptions.SectionName));

ValidateAuthConfig(authOptions);

var provider = builder.Configuration["WakeMesh:Database:Provider"] ?? "SqlServer";
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

var isPostgres = string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

// Resolve the CA path once, before any database work, so a misconfigured Root Certificate fails at
// startup instead of surfacing as an opaque TLS error on the first query. Shared with both
// design-time factories so the published host and `dotnet ef` cannot disagree.
var postgresConnectionString = isPostgres
    ? PostgresConfiguration.ResolveConnectionString(connectionString)
    : connectionString;

// Fail closed outside Development/Test: production PostgreSQL must verify the server certificate.
// Pure validation only — the resolver and the design-time factories keep their existing behavior.
if (isPostgres && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test"))
{
    PostgresConfiguration.EnsureVerifiedTls(postgresConnectionString);
}

builder.Services.AddDbContext<WakeMeshDbContext>(options =>
{
    if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
    }
    else if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(connectionString);
    }
    else if (isPostgres)
    {
        // No EnableRetryOnFailure: retrying execution strategy conflicts with manual transactions.
        // Boot-time Migrate()/EnsureCreated is intentionally not performed here.
        // Shared PG options (migrations assembly + history table in the private schema) come from
        // PostgresConfiguration so the runtime and both design-time factories cannot drift. The CA
        // path was already resolved against AppContext.BaseDirectory above.
        options.UseNpgsql(postgresConnectionString, PostgresConfiguration.Configure);
    }
    else
    {
        throw new InvalidOperationException(
            $"Unknown WakeMesh:Database:Provider '{provider}'. Supported: SqlServer, Sqlite, Postgres.");
    }
});

builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<AlarmGroupService>();
builder.Services.AddScoped<DesiredAckService>();
builder.Services.AddScoped<PushTokenService>();
builder.Services.AddScoped<PushDispatchService>();
builder.Services.AddSingleton<IPushSender, FirebasePushSender>();
builder.Services.AddSingleton<NotificationStatusService>();
builder.Services.AddHostedService<PushDrainHostedService>();
builder.Services.AddSingleton<IAuthorizationHandler, OwnerAuthorizationHandler>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = BuildTokenValidationParameters(authOptions);
        options.MetadataAddress = authOptions.MetadataAddress!;
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                // Do not log token, oid, email, or Authorization header.
                ctx.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("WakeMesh.Auth")
                    .LogWarning("Authentication failed: {ErrorType}", ctx.Exception.GetType().Name);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    var ownerPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new OwnerRequirement())
        .Build();

    options.AddPolicy(AuthPolicies.OwnerOnly, ownerPolicy);
    // Fail closed: every endpoint requires owner auth unless explicitly [AllowAnonymous].
    options.FallbackPolicy = ownerPolicy;
});

builder.Services.AddControllers();

var app = builder.Build();

// X-Content-Type-Options: nosniff on every response. Registered first (before UseExceptionHandler)
// so it also covers 401 challenges and the 500 body the exception handler writes.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Task.CompletedTask;
    });
    await next(context);
});

// Avoid developer exception pages that can echo Authorization headers.
app.UseExceptionHandler(err =>
{
    err.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":"serverError"}""");
    });
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

static void ValidateAuthConfig(AuthOptions auth)
{
    var missing = new List<string>();
    if (string.IsNullOrWhiteSpace(auth.Issuer)) missing.Add(nameof(auth.Issuer));
    if (string.IsNullOrWhiteSpace(auth.Audience)) missing.Add(nameof(auth.Audience));
    if (string.IsNullOrWhiteSpace(auth.RequiredScope)) missing.Add(nameof(auth.RequiredScope));
    if (string.IsNullOrWhiteSpace(auth.AllowedAzp)) missing.Add(nameof(auth.AllowedAzp));
    if (auth.AllowedOwnerOids is null || auth.AllowedOwnerOids.Count == 0)
    {
        missing.Add(nameof(auth.AllowedOwnerOids));
    }

    // Production and hosted configs must discover keys via OIDC metadata (HTTPS only).
    // Tests inject IssuerSigningKey through WebApplicationFactory PostConfigure — never via a PEM config key.
    if (string.IsNullOrWhiteSpace(auth.MetadataAddress))
    {
        missing.Add(nameof(auth.MetadataAddress));
    }
    else if (!Uri.TryCreate(auth.MetadataAddress, UriKind.Absolute, out var metadataUri)
             || !string.Equals(metadataUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "WakeMesh auth MetadataAddress must be an absolute HTTPS URL.");
    }

    if (missing.Count > 0)
    {
        // Do not echo configured secret/oid values.
        throw new InvalidOperationException(
            $"WakeMesh auth configuration is incomplete or fail-closed. Missing: {string.Join(',', missing)}");
    }

    // Single-owner guard: a second distinct non-blank OID silently makes that account a full
    // co-manager of all data (no per-owner isolation). Fail loudly unless explicitly opted in.
    // Report the count only — never the OID values.
    var distinctOwners = (auth.AllowedOwnerOids ?? new List<string>())
        .Where(oid => !string.IsNullOrWhiteSpace(oid))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    if (distinctOwners > 1 && !auth.AllowMultipleOwners)
    {
        throw new InvalidOperationException(
            $"WakeMesh auth configuration lists {distinctOwners} distinct AllowedOwnerOids. Every allowlisted " +
            "OID co-manages all devices and groups (there is no per-owner isolation). Set " +
            "AllowMultipleOwners=true to acknowledge this explicitly, or keep a single owner.");
    }
}

static TokenValidationParameters BuildTokenValidationParameters(AuthOptions auth)
{
    return new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = auth.Issuer,
        ValidateAudience = true,
        ValidAudience = auth.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        ClockSkew = TimeSpan.FromMinutes(2),
        NameClaimType = "oid",
        RoleClaimType = "roles",
    };
}

public partial class Program;
