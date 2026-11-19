using Microsoft.Data.SqlClient;

namespace WakeMesh.Api.Tests;

/// <summary>
/// Regression coverage: SqlClient 7+ requires
/// Microsoft.Data.SqlClient.Extensions.Azure for Authentication=Active Directory Managed Identity.
/// Resolves and initializes the provider only — no token acquisition, DB connect, or external HTTP.
/// </summary>
public sealed class SqlManagedIdentityProviderTests
{
    [Fact]
    public void ActiveDirectoryManagedIdentity_Provider_IsResolved_FromAzureExtension()
    {
        var provider = SqlAuthenticationProvider.GetProvider(
            SqlAuthenticationMethod.ActiveDirectoryManagedIdentity);

        Assert.NotNull(provider);
        Assert.True(
            provider.IsSupported(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity),
            "Resolved provider must advertise support for ActiveDirectoryManagedIdentity.");

        var type = provider.GetType();
        Assert.Equal(
            "Microsoft.Data.SqlClient.ActiveDirectoryAuthenticationProvider",
            type.FullName);
        Assert.Equal(
            "Microsoft.Data.SqlClient.Extensions.Azure",
            type.Assembly.GetName().Name);

        // Alias enum used by some connection-string parsers must resolve to the same provider instance path.
        var msiAliasProvider = SqlAuthenticationProvider.GetProvider(
            SqlAuthenticationMethod.ActiveDirectoryMSI);
        Assert.NotNull(msiAliasProvider);
        Assert.Same(provider.GetType(), msiAliasProvider.GetType());
    }
}
