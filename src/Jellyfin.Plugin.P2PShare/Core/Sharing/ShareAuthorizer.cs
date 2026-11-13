using System;
using System.Collections.Generic;
using System.Net;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Tokens;

namespace Jellyfin.Plugin.P2PShare.Core.Sharing;

/// <summary>Reasons a peer request can be rejected.</summary>
public enum AuthFailure
{
    None,
    SharingDisabled,
    MissingCredentials,
    InvalidCredentials,
    ShareDisabled,
    ShareExpired,
    AddressNotAllowed,
}

/// <summary>Result of authorizing a peer request.</summary>
public sealed record AuthResult(OutgoingShare? Share, AuthFailure Failure)
{
    public bool Succeeded => Failure == AuthFailure.None && Share is not null;

    /// <summary>Gets the HTTP status code that corresponds to the failure.</summary>
    public int StatusCode => Failure switch
    {
        AuthFailure.None => 200,
        AuthFailure.SharingDisabled => 503,
        AuthFailure.MissingCredentials => 401,
        AuthFailure.InvalidCredentials => 401,
        _ => 403,
    };

    /// <summary>Gets a message safe to return to the peer.</summary>
    public string Message => Failure switch
    {
        AuthFailure.None => "OK",
        AuthFailure.SharingDisabled => "Sharing is disabled on this server.",
        AuthFailure.MissingCredentials => "Missing or malformed share credentials.",
        AuthFailure.InvalidCredentials => "Invalid share credentials.",
        AuthFailure.ShareDisabled => "This share is disabled.",
        AuthFailure.ShareExpired => "This share has expired.",
        AuthFailure.AddressNotAllowed => "Requests from this address are not allowed for this share.",
        _ => "Forbidden.",
    };
}

/// <summary>
/// Authenticates peer requests by share credential and checks share state.
/// </summary>
public static class ShareAuthorizer
{
    public static AuthResult Authorize(
        string? credentialHeader,
        IEnumerable<OutgoingShare> shares,
        GlobalSettings settings,
        IPAddress? remoteAddress,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.SharingEnabled)
        {
            return new AuthResult(null, AuthFailure.SharingDisabled);
        }

        if (!ShareCredential.TryParse(credentialHeader, out var shareId, out var secret))
        {
            return new AuthResult(null, AuthFailure.MissingCredentials);
        }

        OutgoingShare? share = null;
        foreach (var s in shares)
        {
            if (s.Id == shareId)
            {
                share = s;
                break;
            }
        }

        // Always perform a hash comparison so unknown ids and bad secrets take similar time.
        var hash = share?.SecretHash ?? ShareSecrets.Hash("unknown-share-placeholder");
        if (!ShareSecrets.Verify(secret, hash) || share is null)
        {
            return new AuthResult(null, AuthFailure.InvalidCredentials);
        }

        if (!share.Enabled)
        {
            return new AuthResult(share, AuthFailure.ShareDisabled);
        }

        if (share.ExpiresAt is DateTime expires && now.UtcDateTime >= DateTime.SpecifyKind(expires, DateTimeKind.Utc))
        {
            return new AuthResult(share, AuthFailure.ShareExpired);
        }

        if (!IsAddressAllowed(share.AllowedNetworks, remoteAddress))
        {
            return new AuthResult(share, AuthFailure.AddressNotAllowed);
        }

        return new AuthResult(share, AuthFailure.None);
    }

    /// <summary>
    /// Returns true if <paramref name="address"/> is inside one of the CIDR ranges,
    /// or if no ranges are configured. Invalid entries are ignored.
    /// </summary>
    public static bool IsAddressAllowed(IReadOnlyCollection<string>? networks, IPAddress? address)
    {
        if (networks is null || networks.Count == 0)
        {
            return true;
        }

        var anyValid = false;
        if (address is not null && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var entry in networks)
        {
            if (!TryParseNetwork(entry, out var network))
            {
                continue;
            }

            anyValid = true;
            if (address is not null && network.Contains(address))
            {
                return true;
            }
        }

        // A list that contains only invalid entries does not lock everyone out.
        return !anyValid;
    }

    /// <summary>Parses "a.b.c.d/n", "::1/128" or a bare address.</summary>
    public static bool TryParseNetwork(string? value, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains('/', StringComparison.Ordinal) && IPAddress.TryParse(trimmed, out var single))
        {
            network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        return IPNetwork.TryParse(trimmed, out network);
    }
}
