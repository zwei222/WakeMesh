using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace WakeMesh.Client.Tests;

/// <summary>
/// Fictional RSA keys and claim values for local JwtBearer tests only.
/// Consumers tenant id is the public Microsoft MSA constant — not a personal value.
/// Duplicated in this test project so API test fixtures are not modified.
/// </summary>
public sealed class ClientTestAuth
{
    public const string Issuer = "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0";
    public const string Audience = "api://wakemesh-api-test";
    public const string AllowedOid = "11111111-1111-1111-1111-111111111111";
    public const string AllowedAzp = "mobile-client-id-test";
    public const string RequiredScope = "access_as_user";
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
        DateTime? expires = null)
    {
        var claims = new List<Claim>
        {
            new("ver", "2.0"),
            new("tid", ConsumersTenantId),
        };
        if (oid is not null) claims.Add(new Claim("oid", oid));
        if (scp is not null) claims.Add(new Claim("scp", scp));
        if (azp is not null) claims.Add(new Claim("azp", azp));

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(SecurityKey, SigningAlgorithm));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
