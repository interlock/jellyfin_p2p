using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// A share granted by this server to a peer. A value of 0 for any limit means "unlimited".
/// </summary>
public class OutgoingShare
{
    /// <summary>Gets or sets the share id (part of the token).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets a label, typically who the share is for.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 hash of the share secret.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the ids of the local libraries (collection folders) included in the share.</summary>
    public List<Guid> LibraryIds { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the share is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets when the share was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the share expires (UTC). Null never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the peer may request server-side transcoding.</summary>
    public bool AllowTranscoding { get; set; }

    /// <summary>Gets or sets the maximum media bitrate in Mbps. Items above it are transcoded down or refused.</summary>
    public double MaxStreamingBitrateMbps { get; set; }

    /// <summary>Gets or sets the maximum throughput across all of this share's streams, in Mbps.</summary>
    public double MaxBandwidthMbps { get; set; }

    /// <summary>Gets or sets the data volume allowed per <see cref="QuotaInterval"/>, in gigabytes.</summary>
    public double QuotaGB { get; set; }

    /// <summary>Gets or sets the quota window.</summary>
    public QuotaInterval QuotaInterval { get; set; } = QuotaInterval.Monthly;

    /// <summary>Gets or sets the maximum number of concurrent streams.</summary>
    public int MaxConcurrentStreams { get; set; } = 2;

    /// <summary>Gets or sets an optional CIDR allowlist (e.g. "203.0.113.0/24"). Empty allows any address.</summary>
    public List<string> AllowedNetworks { get; set; } = new();

    /// <summary>Gets or sets free-text notes.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Gets the usage ledger key of this share.</summary>
    public string UsageKey => UsageKeys.ForShare(Id);
}
