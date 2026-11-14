namespace WakeMesh.Client.Http;

/// <summary>
/// Ensures Authorization and X-Device-Credential are only sent to the original API host.
/// Strips secrets if a relative-path join or misconfigured handler would target another host.
/// </summary>
public sealed class PinnedHostHandler : DelegatingHandler
{
    public const string DeviceCredentialHeaderName = "X-Device-Credential";

    private readonly string _allowedHost;

    public PinnedHostHandler(string allowedHost, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        if (string.IsNullOrWhiteSpace(allowedHost))
        {
            throw new ArgumentException("Allowed host is required.", nameof(allowedHost));
        }

        _allowedHost = allowedHost;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
        {
            StripSecrets(request);
            throw new InvalidOperationException("Request URI must be absolute before sending secrets.");
        }

        if (!string.Equals(request.RequestUri.Host, _allowedHost, StringComparison.OrdinalIgnoreCase))
        {
            StripSecrets(request);
            throw new InvalidOperationException("Request host differs from configured API host; secrets were not sent.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static void StripSecrets(HttpRequestMessage request)
    {
        request.Headers.Authorization = null;
        request.Headers.Remove(DeviceCredentialHeaderName);
    }
}
