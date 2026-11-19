namespace WakeMesh.Client.Http;

/// <summary>Production API base addresses must be absolute HTTPS. In-process TestServer uses a separate HttpClient transport.</summary>
public static class HttpsBaseAddress
{
    public static Uri RequireHttps(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("API base address must be an absolute URI.", nameof(baseAddress));
        }

        if (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("API base address must use HTTPS.", nameof(baseAddress));
        }

        return baseAddress;
    }

    public static Uri RequireHttps(string baseAddress)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("API base address must be an absolute URI.", nameof(baseAddress));
        }

        return RequireHttps(uri);
    }
}
