using System.Net;

namespace WakeMesh.Client.Http;

/// <summary>
/// Disables automatic redirects on the inner handler and fails closed on 3xx so
/// Authorization / X-Device-Credential cannot follow a Location to another host.
/// </summary>
public sealed class NoRedirectHandler : DelegatingHandler
{
    public NoRedirectHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        if (innerHandler is HttpClientHandler clientHandler)
        {
            clientHandler.AllowAutoRedirect = false;
        }
        else if (innerHandler is SocketsHttpHandler socketsHandler)
        {
            socketsHandler.AllowAutoRedirect = false;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
        {
            response.Dispose();
            throw new RedirectRefusedException(
                $"HTTP {(int)response.StatusCode} redirect refused to prevent credential leakage.");
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and < 400;
    }
}

/// <summary>Raised when the API returns a redirect. Callers map this to <see cref="ApiErrorKind.RedirectRefused"/>.</summary>
public sealed class RedirectRefusedException : InvalidOperationException
{
    public RedirectRefusedException(string message) : base(message)
    {
    }
}
