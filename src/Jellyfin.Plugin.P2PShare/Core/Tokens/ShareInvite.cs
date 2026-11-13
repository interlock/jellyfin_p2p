using System;

namespace Jellyfin.Plugin.P2PShare.Core.Tokens;

/// <summary>
/// The information carried by an invite token.
/// </summary>
/// <param name="ServerUrl">Base URL of the sharing server, without trailing slash.</param>
/// <param name="ServerName">Display name of the sharing server.</param>
/// <param name="ServerId">Jellyfin server id of the sharing server.</param>
/// <param name="ShareId">Id of the share on the sharing server.</param>
/// <param name="Secret">The share secret (base64url).</param>
public sealed record ShareInvite(string ServerUrl, string ServerName, string ServerId, Guid ShareId, string Secret)
{
    /// <summary>Gets the value to send in the <c>X-P2PShare-Key</c> header.</summary>
    public string CredentialHeaderValue => ShareCredential.Format(ShareId, Secret);
}
