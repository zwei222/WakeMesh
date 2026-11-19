using System.Net;
using WakeMesh.Client.Models;

namespace WakeMesh.Client.Tests;

public sealed class ClientResilienceTests
{
    [Fact]
    public async Task Timeout_MapsToTimeoutKind()
    {
        var handler = new DelayingHandler(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"));
        var res = await api.ListDevicesAsync();
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.Timeout, res.Kind);
    }

    [Fact]
    public async Task CallerCancellation_MapsToCanceledKind()
    {
        var handler = new DelayingHandler(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var res = await api.ListDevicesAsync(cts.Token);
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.Canceled, res.Kind);
    }

    [Fact]
    public async Task NetworkFailure_MapsToNetworkKind()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"));
        var res = await api.ListDevicesAsync();
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.Network, res.Kind);
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayingHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_ex);
    }
}
