using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.P2PShare.Core.Tokens;

/// <summary>
/// Generates, hashes and verifies share secrets.
/// </summary>
public static class ShareSecrets
{
    /// <summary>Number of random bytes in a secret.</summary>
    public const int SecretBytes = 32;

    /// <summary>Creates a new random secret encoded as base64url.</summary>
    public static string NewSecret() => Base64Url.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Creates a shorter random key (e.g. for local proxy URLs).</summary>
    public static string NewKey(int bytes = 24) => Base64Url.Encode(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Hashes a secret for storage (lower-case hex SHA-256).</summary>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    }

    /// <summary>Verifies a presented secret against a stored hash in constant time.</summary>
    public static bool Verify(string? presentedSecret, string? storedHash)
    {
        if (string.IsNullOrEmpty(presentedSecret) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var presented = Encoding.ASCII.GetBytes(Hash(presentedSecret));
        var stored = Encoding.ASCII.GetBytes(storedHash.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    /// <summary>Compares two keys in constant time.</summary>
    public static bool KeysEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
