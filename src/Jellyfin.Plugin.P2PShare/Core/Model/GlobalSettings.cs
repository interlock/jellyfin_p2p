namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// Server-wide settings. Persisted as part of the plugin configuration.
/// A value of 0 for any limit means "unlimited".
/// </summary>
public class GlobalSettings
{
    // ---- Sharing side -------------------------------------------------------------

    /// <summary>Gets or sets a value indicating whether peer requests are answered at all.</summary>
    public bool SharingEnabled { get; set; } = true;

    /// <summary>Gets or sets the base URL peers use to reach this server. Embedded in new tokens.</summary>
    public string PublicServerUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown to peers. Empty uses the Jellyfin server name.</summary>
    public string ServerDisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether plain-HTTP tokens and peers are refused.</summary>
    public bool RequireHttps { get; set; }

    /// <summary>Gets or sets the total outbound throughput to all peers, in megabits per second.</summary>
    public double GlobalMaxBandwidthMbps { get; set; }

    /// <summary>Gets or sets the total data volume served to peers per <see cref="GlobalQuotaInterval"/>, in gigabytes.</summary>
    public double GlobalQuotaGB { get; set; }

    /// <summary>Gets or sets the window of the global quota.</summary>
    public QuotaInterval GlobalQuotaInterval { get; set; } = QuotaInterval.Monthly;

    /// <summary>Gets or sets the maximum number of concurrent peer streams.</summary>
    public int GlobalMaxConcurrentStreams { get; set; }

    /// <summary>Gets or sets a value indicating whether any share may transcode.</summary>
    public bool AllowTranscoding { get; set; } = true;

    /// <summary>Gets or sets the maximum number of concurrent transcodes for peers.</summary>
    public int MaxConcurrentTranscodes { get; set; } = 2;

    /// <summary>Gets or sets the default lifetime of new shares in days (0 = never expires).</summary>
    public int DefaultTokenLifetimeDays { get; set; }

    // ---- Receiving side -----------------------------------------------------------

    /// <summary>Gets or sets the URL at which this server's ffmpeg can reach its own proxy endpoint.</summary>
    public string LocalProxyBaseUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Gets or sets how often remote libraries are re-synced, in hours.</summary>
    public int SyncIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets a value indicating whether artwork is downloaded into mirror folders.</summary>
    public bool DownloadArtwork { get; set; } = true;

    /// <summary>Gets or sets the name template for mirror libraries. Supports {library} and {server}.</summary>
    public string LibraryNameTemplate { get; set; } = "{library} ({server})";

    /// <summary>Gets or sets the root folder for mirror libraries. Empty uses the plugin data folder.</summary>
    public string MirrorRootPath { get; set; } = string.Empty;
}
