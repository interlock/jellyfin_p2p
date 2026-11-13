using System;

namespace Jellyfin.Plugin.P2PShare.Core.Tokens;

/// <summary>
/// Base64url (RFC 4648 §5) encoding without padding.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is null)
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return false;
        }

        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
