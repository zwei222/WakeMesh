using System.Security.Cryptography;

namespace WakeMesh.Api.Services;

public static class CredentialHasher
{
    public static byte[] GenerateCredential(out string credentialBase64)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        credentialBase64 = Convert.ToBase64String(bytes);
        return SHA256.HashData(bytes);
    }

    public static bool Matches(string credentialBase64, byte[] storedHash)
    {
        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(credentialBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (presented.Length != 32)
        {
            return false;
        }

        var hash = SHA256.HashData(presented);
        return CryptographicOperations.FixedTimeEquals(hash, storedHash);
    }
}
