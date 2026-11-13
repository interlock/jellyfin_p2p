using System;

namespace Jellyfin.Plugin.P2PShare.Core.Tokens;

/// <summary>
/// The credential presented by a receiving server in the <c>X-P2PShare-Key</c> header:
/// <c>{shareId:N}.{secret}</c>.
/// </summary>
public static class ShareCredential
{
    /// <summary>Name of the HTTP header carrying the credential.</summary>
    public const string HeaderName = "X-P2PShare-Key";

    public static string Format(Guid shareId, string secret) => shareId.ToString("N") + "." + secret;

    public static bool TryParse(string? value, out Guid shareId, out string secret)
    {
        shareId = Guid.Empty;
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == trimmed.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParse(trimmed.AsSpan(0, dot), out shareId))
        {
            return false;
        }

        secret = trimmed[(dot + 1)..];
        return true;
    }
}
