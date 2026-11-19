using System.Net;
using System.Net.Http.Headers;

namespace WakeMesh.Api.Tests;

public sealed class AuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AuthTests(ApiFactory factory) => _factory = factory;

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task Health_AllowsAnonymous()
    {
        var client = CreateClient();
        var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_NoToken_Returns401()
    {
        var client = CreateClient();
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_BadSignature_Returns401()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(signWithThisKey: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WrongIssuer_Returns401()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(issuer: "https://evil.example/v2.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WrongAudience_Returns401()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(audience: "api://other");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_Expired_Returns401()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_IdTokenStyle_Returns401()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateIdTokenStyle();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_GraphToken_Returns401()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateGraphStyleToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WrongScope_Returns403()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(scp: "other.scope");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WrongAzp_Returns403()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(azp: TestAuthFixture.OtherAzp);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_OidNotAllowlisted_Returns403()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(oid: TestAuthFixture.OtherOid);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_ValidOwnerToken_IsAllowed()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_AppidWithoutAzp_IsForbidden()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(
            azp: null,
            extraClaims: new[] { new System.Security.Claims.Claim("appid", TestAuthFixture.AllowedAzp) });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_ObjectIdentifierWithoutOid_IsForbidden()
    {
        var client = CreateClient();
        var token = _factory.Auth.CreateAccessToken(
            oid: null,
            extraClaims: new[]
            {
                new System.Security.Claims.Claim(
                    "http://schemas.microsoft.com/identity/claims/objectidentifier",
                    TestAuthFixture.AllowedOid)
            });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/v1/alarm-groups");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_MissingVerOrTid_IsForbidden()
    {
        var client = CreateClient();
        var noVer = _factory.Auth.CreateAccessToken(ver: null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", noVer);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/alarm-groups")).StatusCode);

        var noTid = _factory.Auth.CreateAccessToken(tid: null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", noTid);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/alarm-groups")).StatusCode);

        var badVer = _factory.Auth.CreateAccessToken(ver: "1.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", badVer);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/alarm-groups")).StatusCode);
    }
}

