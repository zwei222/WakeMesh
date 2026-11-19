using System.Net;
using System.Reflection;

namespace WakeMesh.Client.Http;

/// <summary>
/// Builds production HttpClient instances with HTTPS base URL, no redirects, and host-pinned secrets.
/// Public surface does not accept arbitrary <see cref="HttpClient"/> or <see cref="HttpMessageHandler"/> instances.
/// </summary>
public static class WakeMeshHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClient for real network use. <paramref name="baseAddress"/> must be absolute HTTPS.
    /// Always constructs an innermost handler with <c>AllowAutoRedirect = false</c>.
    /// </summary>
    public static HttpClient Create(Uri baseAddress, TimeSpan? timeout = null)
    {
        var https = HttpsBaseAddress.RequireHttps(baseAddress);
        var sockets = CreateProductionSocketsHandler();
        var pinned = new PinnedHostHandler(https.Host, new NoRedirectHandler(sockets));
        return new HttpClient(pinned)
        {
            BaseAddress = EnsureTrailingSlash(https),
            Timeout = timeout ?? TimeSpan.FromSeconds(100),
        };
    }

    public static HttpClient Create(string baseAddress, TimeSpan? timeout = null)
        => Create(HttpsBaseAddress.RequireHttps(baseAddress), timeout);

    /// <summary>Innermost production sockets handler: redirects disabled.</summary>
    public static SocketsHttpHandler CreateProductionSocketsHandler()
        => new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

    /// <summary>Test hook — same as <see cref="CreateProductionSocketsHandler"/>.</summary>
    internal static SocketsHttpHandler CreateProductionSocketsHandlerForTests()
        => CreateProductionSocketsHandler();

    /// <summary>
    /// Test-only: wrap a custom inner handler while still forcing no-redirect + optional host pin.
    /// Forces <see cref="HttpClientHandler.AllowAutoRedirect"/> / <see cref="SocketsHttpHandler.AllowAutoRedirect"/> false when applicable.
    /// </summary>
    internal static HttpClient CreateForTests(Uri baseAddress, HttpMessageHandler innerHandler, string? pinHost = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        ForceDisableAutoRedirect(innerHandler);
        HttpMessageHandler pipeline = new NoRedirectHandler(innerHandler);
        if (!string.IsNullOrWhiteSpace(pinHost))
        {
            pipeline = new PinnedHostHandler(pinHost, pipeline);
        }

        return new HttpClient(pipeline)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(100),
        };
    }

    internal static void ForceDisableAutoRedirect(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case HttpClientHandler httpClientHandler:
                httpClientHandler.AllowAutoRedirect = false;
                break;
            case SocketsHttpHandler socketsHttpHandler:
                socketsHttpHandler.AllowAutoRedirect = false;
                break;
            case DelegatingHandler delegating:
                if (delegating.InnerHandler is not null)
                {
                    ForceDisableAutoRedirect(delegating.InnerHandler);
                }

                break;
        }

        // Walk uncommon custom wrappers via public InnerHandler property if present.
        var prop = handler.GetType().GetProperty("InnerHandler", BindingFlags.Instance | BindingFlags.Public);
        if (prop?.GetValue(handler) is HttpMessageHandler inner && !ReferenceEquals(inner, handler))
        {
            ForceDisableAutoRedirect(inner);
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var s = uri.AbsoluteUri;
        if (!s.EndsWith('/'))
        {
            s += "/";
        }

        return new Uri(s, UriKind.Absolute);
    }
}
