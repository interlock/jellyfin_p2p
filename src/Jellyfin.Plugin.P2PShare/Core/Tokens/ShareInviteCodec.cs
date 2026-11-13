using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.P2PShare.Core.Tokens;

/// <summary>
/// Encodes and decodes invite tokens: <c>jfp2p1.&lt;base64url(json)&gt;.&lt;checksum&gt;</c>.
/// </summary>
public static class ShareInviteCodec
{
    /// <summary>Token prefix, including the format version.</summary>
    public const string Prefix = "jfp2p1";

    private const int ChecksumLength = 8;

    public static string Encode(ShareInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        if (!UrlHelper.TryNormalizeServerUrl(invite.ServerUrl, out var url))
        {
            throw new ArgumentException("Server URL must be an absolute http(s) URL.", nameof(invite));
        }

        if (string.IsNullOrEmpty(invite.Secret))
        {
            throw new ArgumentException("Secret is required.", nameof(invite));
        }

        var payload = new Payload
        {
            Version = 1,
            Url = url,
            Name = invite.ServerName ?? string.Empty,
            ServerId = invite.ServerId ?? string.Empty,
            ShareId = invite.ShareId.ToString("N"),
            Secret = invite.Secret,
        };

        var body = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return Prefix + "." + body + "." + Checksum(body);
    }

    public static bool TryDecode(string? token, out ShareInvite? invite, out string? error)
    {
        invite = null;
        error = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Token is empty.";
            return false;
        }

        // Tolerate whitespace/newlines introduced by copy & paste.
        var compact = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (!char.IsWhiteSpace(c))
            {
                compact.Append(c);
            }
        }

        var parts = compact.ToString().Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            error = "Not a P2P Share token (expected 'jfp2p1.<data>.<checksum>').";
            return false;
        }

        if (!string.Equals(Checksum(parts[1]), parts[2], StringComparison.Ordinal))
        {
            error = "Token checksum mismatch; it may have been truncated or altered.";
            return false;
        }

        if (!Base64Url.TryDecode(parts[1], out var json))
        {
            error = "Token data is not valid base64url.";
            return false;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json);
        }
        catch (JsonException)
        {
            error = "Token data is not valid JSON.";
            return false;
        }

        if (payload is null || payload.Version != 1)
        {
            error = "Unsupported token version.";
            return false;
        }

        if (!UrlHelper.TryNormalizeServerUrl(payload.Url, out var url))
        {
            error = "Token contains an invalid server URL.";
            return false;
        }

        if (!Guid.TryParse(payload.ShareId, out var shareId) || shareId == Guid.Empty)
        {
            error = "Token contains an invalid share id.";
            return false;
        }

        if (string.IsNullOrEmpty(payload.Secret))
        {
            error = "Token has no secret.";
            return false;
        }

        invite = new ShareInvite(url, payload.Name ?? string.Empty, payload.ServerId ?? string.Empty, shareId, payload.Secret);
        return true;
    }

    private static string Checksum(string body)
        => Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..ChecksumLength];

    private sealed class Payload
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("u")]
        public string? Url { get; set; }

        [JsonPropertyName("n")]
        public string? Name { get; set; }

        [JsonPropertyName("sid")]
        public string? ServerId { get; set; }

        [JsonPropertyName("i")]
        public string? ShareId { get; set; }

        [JsonPropertyName("s")]
        public string? Secret { get; set; }
    }
}
