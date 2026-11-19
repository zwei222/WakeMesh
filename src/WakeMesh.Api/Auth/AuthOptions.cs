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

    /// <summary>
    /// Explicit opt-in to more than one distinct owner OID. Every allowlisted OID co-manages all
    /// devices and groups (there is no per-owner isolation). Default false: a second distinct OID
    /// fails at startup instead of silently granting co-management of all data.
    /// </summary>
    public bool AllowMultipleOwners { get; set; }

    /// <summary>OpenID metadata URL. Required for signing-key discovery in all hosted environments.</summary>
    public string? MetadataAddress { get; set; }
}
