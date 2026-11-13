using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.P2PShare.Core.Model;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

// Wire contract of the peer API (protocol version 1). Both servers serialize these with
// RemoteJson.Options. Add fields only in a backwards-compatible way.

/// <summary>JSON settings for the peer API.</summary>
public static class RemoteJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>Peer API route templates (relative to the server base URL).</summary>
public static class RemoteApiRoutes
{
    public const int ProtocolVersion = 1;
    public const string Info = "P2PShare/Remote/Info";
    public const string Libraries = "P2PShare/Remote/Libraries";

    public static string Items(Guid libraryId, int startIndex, int limit)
        => $"P2PShare/Remote/Libraries/{libraryId:N}/Items?startIndex={startIndex}&limit={limit}";

    public static string Image(Guid itemId, string imageType) => $"P2PShare/Remote/Items/{itemId:N}/Images/{Uri.EscapeDataString(imageType)}";

    public static string Stream(Guid itemId) => $"P2PShare/Remote/Items/{itemId:N}/Stream";
}

/// <summary>Error body returned by the peer API.</summary>
public sealed class RemoteError
{
    public string Error { get; set; } = string.Empty;
}

/// <summary>Response of <c>GET /Remote/Info</c>.</summary>
public sealed class RemoteServerInfo
{
    public int ProtocolVersion { get; set; } = RemoteApiRoutes.ProtocolVersion;

    public string ServerName { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public RemoteSharePolicy Share { get; set; } = new();
}

/// <summary>The policy and usage of the share, as seen by the peer.</summary>
public sealed class RemoteSharePolicy
{
    public string Name { get; set; } = string.Empty;

    public bool AllowTranscoding { get; set; }

    public double MaxStreamingBitrateMbps { get; set; }

    public double MaxBandwidthMbps { get; set; }

    public double QuotaGB { get; set; }

    public QuotaInterval QuotaInterval { get; set; }

    public long QuotaUsedBytes { get; set; }

    public DateTimeOffset? QuotaResetsAt { get; set; }

    public int MaxConcurrentStreams { get; set; }

    public DateTime? ExpiresAt { get; set; }
}

/// <summary>A library offered by the share.</summary>
public sealed class RemoteLibrary
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin collection type (movies, tvshows, music, musicvideos, homevideos, mixed).</summary>
    public string CollectionType { get; set; } = string.Empty;

    public int ItemCount { get; set; }
}

/// <summary>A page of items.</summary>
public sealed class RemoteItemPage
{
    public List<RemoteItem> Items { get; set; } = new();

    public int TotalRecordCount { get; set; }

    public int StartIndex { get; set; }
}

/// <summary>An image available for an item.</summary>
public sealed class RemoteImage
{
    /// <summary>Gets or sets the image type: Primary, Backdrop or Thumb.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets a value that changes when the image changes.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Gets or sets the file extension including the dot, e.g. ".jpg".</summary>
    public string Extension { get; set; } = ".jpg";
}

/// <summary>Remote item types the mirror understands.</summary>
public static class RemoteItemTypes
{
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string Season = "Season";
    public const string Episode = "Episode";
    public const string Audio = "Audio";
    public const string MusicAlbum = "MusicAlbum";
    public const string MusicVideo = "MusicVideo";
    public const string Video = "Video";
}

/// <summary>Catalog entry for one item.</summary>
public sealed class RemoteItem
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? OriginalTitle { get; set; }

    public string? Overview { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime? PremiereDate { get; set; }

    public int? IndexNumber { get; set; }

    public int? IndexNumberEnd { get; set; }

    public int? ParentIndexNumber { get; set; }

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    public List<string> Artists { get; set; } = new();

    public List<string> Genres { get; set; } = new();

    public List<string> Studios { get; set; } = new();

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the total bitrate in bits/s.</summary>
    public long? Bitrate { get; set; }

    public string? Container { get; set; }

    public long? Size { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<RemoteImage> Images { get; set; } = new();

    public DateTime? DateModified { get; set; }

    public bool IsPlayable => Type is RemoteItemTypes.Movie or RemoteItemTypes.Episode or RemoteItemTypes.Audio or RemoteItemTypes.MusicVideo or RemoteItemTypes.Video;
}
