using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// A share received from a peer (one pasted token).
/// </summary>
public class IncomingConnection
{
    /// <summary>Gets or sets the local id of the connection.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the invite token as pasted.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's display name (from the token, refreshed on sync).</summary>
    public string RemoteServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's base URL (from the token).</summary>
    public string RemoteServerUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether sync and proxying are active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the random key embedded in local .strm URLs.</summary>
    public string ProxyKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to ask the peer to transcode.</summary>
    public bool PreferTranscode { get; set; }

    /// <summary>Gets or sets the target bitrate (Mbps) when transcoding. 0 lets the peer decide.</summary>
    public double PreferredMaxBitrateMbps { get; set; }

    /// <summary>Gets or sets the remote libraries offered by the peer and how they are mirrored.</summary>
    public List<MirroredLibrary> Libraries { get; set; } = new();

    /// <summary>Gets or sets when the last sync finished (UTC).</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>Gets or sets a human-readable result of the last sync.</summary>
    public string LastSyncStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the last sync failed.</summary>
    public bool LastSyncFailed { get; set; }
}
