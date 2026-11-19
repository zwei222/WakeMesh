using System.Net;
using System.Net.Http.Headers;
using WakeMesh.Client.Http;

namespace WakeMesh.Client.Tests;

public sealed class HttpSafetyTests
{
    [Fact]
    public void ProductionFactory_RejectsHttpBaseAddress()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WakeMeshHttpClientFactory.Create("http://example.test/api/"));
        Assert.Contains("HTTPS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionFactory_RejectsRelativeUri()
    {
        Assert.Throws<ArgumentException>(() => HttpsBaseAddress.RequireHttps("/api/"));
    }

    [Fact]
    public void ProductionFactory_AcceptsHttps()
    {
        using var client = WakeMeshHttpClientFactory.Create("https://api.example.test/");
        Assert.Equal("https", client.BaseAddress!.Scheme);
    }

    [Fact]
    public async Task NoRedirectHandler_Refuses3xx_WithoutFollowing()
    {
        var inner = new StubHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/steal") },
        });
        var handler = new NoRedirectHandler(inner);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");

        await Assert.ThrowsAsync<RedirectRefusedException>(() => http.GetAsync("/v1/x"));
        Assert.Equal(1, inner.Calls);
        Assert.DoesNotContain("evil", inner.LastRequestUri?.Host ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PinnedHostHandler_StripsSecrets_WhenHostDiffers()
    {
        var inner = new CaptureHandler();
        var pinned = new PinnedHostHandler("api.example.test", inner);
        using var http = new HttpClient(pinned);

        var req = new HttpRequestMessage(HttpMethod.Get, "https://other.example/path");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner-token");
        req.Headers.TryAddWithoutValidation(PinnedHostHandler.DeviceCredentialHeaderName, "device-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(req));
        Assert.Null(inner.LastAuthorization);
        Assert.False(inner.HadDeviceCredential);
    }

    [Fact]
    public async Task PinnedHostHandler_AllowsSameHost_WithSecrets()
    {
        var inner = new CaptureHandler { Response = new HttpResponseMessage(HttpStatusCode.OK) };
        var pinned = new PinnedHostHandler("api.example.test", inner);
        using var http = new HttpClient(pinned);

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/api/v1/devices");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner-token");
        req.Headers.TryAddWithoutValidation(PinnedHostHandler.DeviceCredentialHeaderName, "device-secret");

        using var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Bearer", inner.LastAuthorization?.Scheme);
        Assert.True(inner.HadDeviceCredential);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int Calls { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_response);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public bool HadDeviceCredential { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization;
            HadDeviceCredential = request.Headers.Contains(PinnedHostHandler.DeviceCredentialHeaderName);
            return Task.FromResult(Response);
        }
    }
}
