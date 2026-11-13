using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Sharing side: exposes local libraries and items to peers, enforcing share scope.
/// </summary>
public sealed class CatalogService
{
    /// <summary>Item kinds included in a shared catalog.</summary>
    public static readonly BaseItemKind[] SharedKinds =
    {
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Episode,
        BaseItemKind.Audio,
        BaseItemKind.MusicAlbum,
        BaseItemKind.MusicVideo,
        BaseItemKind.Video,
    };

    private static readonly ImageType[] SharedImageTypes = { ImageType.Primary, ImageType.Backdrop, ImageType.Thumb };

    private readonly ILibraryManager _libraryManager;
    private readonly ConfigStore _config;

    public CatalogService(ILibraryManager libraryManager, ConfigStore config)
    {
        _libraryManager = libraryManager;
        _config = config;
    }

    /// <summary>A local library as shown in the admin UI.</summary>
    public sealed record LocalLibrary(Guid Id, string Name, string CollectionType, bool IsMirror);

    /// <summary>Lists local libraries, flagging mirrors of remote libraries (which cannot be re-shared).</summary>
    public IReadOnlyList<LocalLibrary> GetLocalLibraries()
    {
        var mirrorRoot = NormalizeDir(_config.MirrorRoot);
        var result = new List<LocalLibrary>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            var isMirror = folder.Locations.Any(l => NormalizeDir(l).StartsWith(mirrorRoot, StringComparison.OrdinalIgnoreCase));
            result.Add(new LocalLibrary(id, folder.Name, folder.CollectionType?.ToString() ?? "mixed", isMirror));
        }

        return result;
    }

    /// <summary>Libraries of a share that still exist and are not mirrors.</summary>
    public List<RemoteLibrary> GetSharedLibraries(OutgoingShare share)
    {
        var allowed = new HashSet<Guid>(share.LibraryIds);
        var list = new List<RemoteLibrary>();
        foreach (var lib in GetLocalLibraries().Where(l => allowed.Contains(l.Id) && !l.IsMirror))
        {
            var folder = _libraryManager.GetItemById(lib.Id);
            var count = folder is null ? 0 : _libraryManager.GetCount(BuildQuery(folder, playableOnly: true));
            list.Add(new RemoteLibrary { Id = lib.Id, Name = lib.Name, CollectionType = lib.CollectionType, ItemCount = count });
        }

        return list;
    }

    /// <summary>Ids of the share's libraries that still exist and are not mirrors (cheap; no counts).</summary>
    public HashSet<Guid> GetSharedLibraryIds(OutgoingShare share)
    {
        var allowed = new HashSet<Guid>(share.LibraryIds);
        return GetLocalLibraries().Where(l => allowed.Contains(l.Id) && !l.IsMirror).Select(l => l.Id).ToHashSet();
    }

    /// <summary>A page of items in a shared library, or null if the library is not in the share.</summary>
    public RemoteItemPage? GetItems(OutgoingShare share, Guid libraryId, int startIndex, int limit)
    {
        if (!GetSharedLibraryIds(share).Contains(libraryId))
        {
            return null;
        }

        var folder = _libraryManager.GetItemById(libraryId);
        if (folder is null)
        {
            return null;
        }

        var query = BuildQuery(folder, playableOnly: false);
        query.StartIndex = Math.Max(0, startIndex);
        query.Limit = Math.Clamp(limit, 1, 1000);
        var result = _libraryManager.GetItemsResult(query);

        return new RemoteItemPage
        {
            StartIndex = query.StartIndex.Value,
            TotalRecordCount = result.TotalRecordCount,
            Items = result.Items.Where(i => !IsMirrored(i)).Select(ToRemoteItem).ToList(),
        };
    }

    /// <summary>Returns the item if it exists and belongs to one of the share's libraries.</summary>
    public BaseItem? ResolveItem(OutgoingShare share, Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item.IsVirtualItem || !SharedKinds.Contains(item.GetBaseItemKind()))
        {
            return null;
        }

        var allowed = GetSharedLibraryIds(share);
        return _libraryManager.GetCollectionFolders(item).Any(f => allowed.Contains(f.Id)) ? item : null;
    }

    /// <summary>True for items backed by .strm/shortcuts or remote URLs (never served to peers).</summary>
    public static bool IsMirrored(BaseItem item)
        => item.IsShortcut
           || (!string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the local file path of a playable item, or null if it cannot be served.</summary>
    public static string? GetServablePath(BaseItem item)
    {
        if (IsMirrored(item) || !item.IsFileProtocol || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return null;
        }

        return item.Path;
    }

    /// <summary>Returns the local path of an image, or null.</summary>
    public static string? GetImagePath(BaseItem item, ImageType type)
    {
        var info = item.GetImageInfo(type, 0);
        return info is not null && info.IsLocalFile && File.Exists(info.Path) ? info.Path : null;
    }

    /// <summary>Estimates the total bitrate in bits/s.</summary>
    public static long EstimateBitrate(BaseItem item)
    {
        if (item.TotalBitrate is int b && b > 0)
        {
            return b;
        }

        if (item.Size is long size && size > 0 && item.RunTimeTicks is long ticks && ticks > 0)
        {
            return (long)(size * 8 / TimeSpan.FromTicks(ticks).TotalSeconds);
        }

        return 0;
    }

    public static RemoteItem ToRemoteItem(BaseItem item)
    {
        var r = new RemoteItem
        {
            Id = item.Id,
            Type = MapKind(item.GetBaseItemKind()),
            Name = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            Overview = item.Overview,
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,
            Genres = item.Genres?.ToList() ?? new List<string>(),
            Studios = item.Studios?.ToList() ?? new List<string>(),
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            RunTimeTicks = item.RunTimeTicks,
            Bitrate = EstimateBitrate(item) is var bitrate && bitrate > 0 ? bitrate : null,
            Container = item.Container,
            Size = item.Size,
            DateModified = item.DateModified,
        };

        if (item.ProviderIds is not null)
        {
            foreach (var (k, v) in item.ProviderIds)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    r.ProviderIds[k] = v;
                }
            }
        }

        switch (item)
        {
            case Episode e:
                r.IndexNumberEnd = e.IndexNumberEnd;
                r.SeriesId = e.SeriesId == Guid.Empty ? null : e.SeriesId;
                r.SeriesName = e.SeriesName;
                break;
            case Audio a:
                r.Album = a.Album;
                r.AlbumArtist = a.AlbumArtists?.FirstOrDefault();
                r.Artists = a.Artists?.ToList() ?? new List<string>();
                break;
            case MusicAlbum m:
                r.AlbumArtist = m.AlbumArtist;
                r.Artists = m.Artists?.ToList() ?? new List<string>();
                break;
            case MusicVideo mv:
                r.Album = mv.Album;
                r.Artists = mv.Artists?.ToList() ?? new List<string>();
                break;
        }

        foreach (var type in SharedImageTypes)
        {
            var info = item.GetImageInfo(type, 0);
            if (info is null || !info.IsLocalFile)
            {
                continue;
            }

            r.Images.Add(new RemoteImage
            {
                Type = type.ToString(),
                Tag = info.DateModified.Ticks.ToString("x", CultureInfo.InvariantCulture),
                Extension = string.IsNullOrEmpty(Path.GetExtension(info.Path)) ? ".jpg" : Path.GetExtension(info.Path).ToLowerInvariant(),
            });
        }

        return r;
    }

    private static string MapKind(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie => RemoteItemTypes.Movie,
        BaseItemKind.Series => RemoteItemTypes.Series,
        BaseItemKind.Episode => RemoteItemTypes.Episode,
        BaseItemKind.Audio => RemoteItemTypes.Audio,
        BaseItemKind.MusicAlbum => RemoteItemTypes.MusicAlbum,
        BaseItemKind.MusicVideo => RemoteItemTypes.MusicVideo,
        _ => RemoteItemTypes.Video,
    };

    private static InternalItemsQuery BuildQuery(BaseItem folder, bool playableOnly)
    {
        return new InternalItemsQuery
        {
            Parent = folder,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = playableOnly
                ? new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.MusicVideo, BaseItemKind.Video }
                : SharedKinds,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true),
        };
    }

    private static string NormalizeDir(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
