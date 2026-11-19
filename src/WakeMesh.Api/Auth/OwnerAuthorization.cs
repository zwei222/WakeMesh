using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace WakeMesh.Api.Auth;

public static class AuthPolicies
{
    public const string OwnerOnly = "OwnerOnly";
}

public sealed class OwnerRequirement : IAuthorizationRequirement
{
}

public sealed class OwnerAuthorizationHandler : AuthorizationHandler<OwnerRequirement>
{
    private readonly AuthOptions _options;

    public OwnerAuthorizationHandler(IOptions<AuthOptions> options)
    {
        _options = options.Value;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnerRequirement requirement)
    {
        // Fail closed when allowlist is empty/unset.
        if (_options.AllowedOwnerOids is null || _options.AllowedOwnerOids.Count == 0)
        {
            return Task.CompletedTask;
        }

        // v2 access tokens only: require short claim names (MapInboundClaims=false).
        // Do not accept appid / long-form objectidentifier substitutes.
        var ver = context.User.FindFirstValue("ver");
        if (!string.Equals(ver, "2.0", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var tid = context.User.FindFirstValue("tid");
        if (string.IsNullOrWhiteSpace(tid))
        {
            return Task.CompletedTask;
        }

        var oid = context.User.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(oid))
        {
            return Task.CompletedTask;
        }

        var scp = context.User.FindFirstValue("scp") ?? "";
        var scopes = scp.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(_options.RequiredScope, StringComparer.Ordinal))
        {
            return Task.CompletedTask;
        }

        var azp = context.User.FindFirstValue("azp");
        if (string.IsNullOrWhiteSpace(azp) ||
            !string.Equals(azp, _options.AllowedAzp, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        if (!_options.AllowedOwnerOids.Contains(oid, StringComparer.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
