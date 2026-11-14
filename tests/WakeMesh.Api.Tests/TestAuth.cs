using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Fictional RSA keys and claim values for local JwtBearer tests only.
/// Tenant id is the public Microsoft consumers (MSA) tenant constant — not a personal value.
/// </summary>
public sealed class TestAuthFixture
{
    public const string Issuer = "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0";
    public const string Audience = "api://wakemesh-api-test";
    public const string AllowedOid = "11111111-1111-1111-1111-111111111111";
    public const string OtherOid = "22222222-2222-2222-2222-222222222222";
    public const string AllowedAzp = "mobile-client-id-test";
    public const string OtherAzp = "other-client-id-test";
    public const string RequiredScope = "access_as_user";
    /// <summary>Public MSA consumers tenant id (well-known constant).</summary>
    public const string ConsumersTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public RSA Rsa { get; } = RSA.Create(2048);
    public RsaSecurityKey SecurityKey => new(Rsa);
    public string SigningAlgorithm => SecurityAlgorithms.RsaSha256;

    public string CreateAccessToken(
        string? oid = AllowedOid,
        string? scp = RequiredScope,
        string? azp = AllowedAzp,
        string? issuer = Issuer,
        string? audience = Audience,
        string? ver = "2.0",
        string? tid = ConsumersTenantId,
        DateTime? expires = null,
        DateTime? notBefore = null,
        IEnumerable<Claim>? extraClaims = null,
        bool signWithThisKey = true)
    {
        var claims = new List<Claim>();
        if (ver is not null)
        {
            claims.Add(new Claim("ver", ver));
        }

        if (tid is not null)
        {
            claims.Add(new Claim("tid", tid));
        }

        if (oid is not null)
        {
            claims.Add(new Claim("oid", oid));
        }

        if (scp is not null)
        {
            claims.Add(new Claim("scp", scp));
        }

        if (azp is not null)
        {
            claims.Add(new Claim("azp", azp));
        }

        if (extraClaims is not null)
        {
            claims.AddRange(extraClaims);
        }

        var creds = signWithThisKey
            ? new SigningCredentials(SecurityKey, SigningAlgorithm)
            : new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)), SigningAlgorithm);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateIdTokenStyle()
    {
        // ID tokens typically lack the API access scope and use a different audience shape.
        return CreateAccessToken(
            scp: null,
            audience: AllowedAzp,
            extraClaims: new[] { new Claim("preferred_username", "user@example.test") });
    }

    public string CreateGraphStyleToken()
    {
        return CreateAccessToken(
            scp: "User.Read",
            audience: "00000003-0000-0000-c000-000000000000");
    }
}
