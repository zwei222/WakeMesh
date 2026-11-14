namespace WakeMesh.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "WakeMesh:Auth";

    /// <summary>Exact token issuer, e.g. https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0</summary>
    public string Issuer { get; set; } = "";

    /// <summary>API audience (Application ID URI).</summary>
    public string Audience { get; set; } = "";

    /// <summary>Required delegated scope (scp claim).</summary>
    public string RequiredScope { get; set; } = "access_as_user";

    /// <summary>Allowed public client application id (azp).</summary>
    public string AllowedAzp { get; set; } = "";

    /// <summary>Owner object ids (oid). Empty = fail closed.</summary>
    public List<string> AllowedOwnerOids { get; set; } = new();

    /// <summary>OpenID metadata URL. Required for signing-key discovery in all hosted environments.</summary>
    public string? MetadataAddress { get; set; }
}
