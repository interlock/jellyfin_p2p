using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>A reference to a remote image to download.</summary>
public sealed record RemoteImageRef(Guid ItemId, string ImageType, string Tag);

/// <summary>One file the mirror folder should contain.</summary>
public sealed record DesiredFile(string RelativePath, string? Text, RemoteImageRef? Image)
{
    /// <summary>Gets a fingerprint that changes when the file content should change.</summary>
    public string Fingerprint => Text is not null
        ? "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text))).ToLowerInvariant()
        : "img:" + Image!.ItemId.ToString("N") + ":" + Image.ImageType + ":" + Image.Tag;

    public static DesiredFile ForText(string path, string text) => new(path, text, null);

    public static DesiredFile ForImage(string path, RemoteImageRef image) => new(path, null, image);
}

/// <summary>Options for planning a mirror.</summary>
public sealed record MirrorOptions(ProxyUrlBuilder Urls, bool DownloadArtwork);

/// <summary>
/// Turns a remote catalog into the set of files (.strm, .nfo, artwork) of a mirror folder,
/// following Jellyfin naming conventions.
/// </summary>
public static class MirrorPlanner
{
    public static List<DesiredFile> Plan(IReadOnlyCollection<RemoteItem> items, MirrorOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<DesiredFile>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var series = items.Where(i => i.Type == RemoteItemTypes.Series).ToDictionary(i => i.Id);
        var seriesFolders = new Dictionary<Guid, string>();

        // Deterministic order so collisions are always resolved the same way.
        var ordered = items.OrderBy(i => TypeOrder(i.Type)).ThenBy(i => i.Id).ToList();

        foreach (var s in ordered.Where(i => i.Type == RemoteItemTypes.Series))
        {
            var folder = Unique(used, SeriesFolderName(s), s.Id);
            seriesFolders[s.Id] = folder;
            files.Add(DesiredFile.ForText(folder + "/tvshow.nfo", NfoWriter.TvShow(s)));
            AddArtwork(files, options, s, folder + "/poster", folder + "/fanart");
        }

        foreach (var item in ordered)
        {
            switch (item.Type)
            {
                case RemoteItemTypes.Movie:
                    {
                        var name = TitleWithYear(item);
                        var folder = Unique(used, name + ProviderTag(item, "tmdb", "imdb"), item.Id);
                        var basePath = folder + "/" + FileNameSanitizer.Sanitize(name);
                        AddPlayable(files, options, item, basePath, NfoWriter.Movie(item));
                        AddArtwork(files, options, item, folder + "/poster", folder + "/fanart");
                        break;
                    }

                case RemoteItemTypes.Episode:
                    {
                        var folder = ResolveSeriesFolder(item, seriesFolders, series, used);
                        var seasonFolder = item.ParentIndexNumber is int season
                            ? folder + "/Season " + season.ToString("00", CultureInfo.InvariantCulture)
                            : folder;
                        var basePath = Unique(used, seasonFolder + "/" + EpisodeFileName(item), item.Id, isPath: true);
                        AddPlayable(files, options, item, basePath, NfoWriter.Episode(item));
                        if (options.DownloadArtwork && FindImage(item, "Primary") is RemoteImage thumb)
                        {
                            files.Add(DesiredFile.ForImage(basePath + "-thumb" + thumb.Extension, new RemoteImageRef(item.Id, thumb.Type, thumb.Tag)));
                        }

                        break;
                    }

                case RemoteItemTypes.MusicVideo:
                    {
                        var artist = item.Artists.FirstOrDefault();
                        var name = string.IsNullOrWhiteSpace(artist) ? item.Name : artist + " - " + item.Name;
                        var basePath = Unique(used, FileNameSanitizer.Sanitize(name), item.Id);
                        AddPlayable(files, options, item, basePath, NfoWriter.MusicVideo(item));
                        AddArtwork(files, options, item, basePath + "-poster", basePath + "-fanart");
                        break;
                    }

                case RemoteItemTypes.Video:
                    {
                        var basePath = Unique(used, FileNameSanitizer.Sanitize(TitleWithYear(item)), item.Id);
                        AddPlayable(files, options, item, basePath, NfoWriter.Video(item));
                        AddArtwork(files, options, item, basePath + "-poster", basePath + "-fanart");
                        break;
                    }

                case RemoteItemTypes.Audio:
                    {
                        var folder = AlbumFolder(item.AlbumArtist ?? item.Artists.FirstOrDefault(), item.Album);
                        var track = item.IndexNumber is int n ? n.ToString("00", CultureInfo.InvariantCulture) + " - " : string.Empty;
                        if (item.ParentIndexNumber is int disc && disc > 1)
                        {
                            track = disc.ToString(CultureInfo.InvariantCulture) + "-" + track;
                        }

                        var basePath = Unique(used, folder + "/" + FileNameSanitizer.Sanitize(track + item.Name), item.Id, isPath: true);
                        files.Add(DesiredFile.ForText(basePath + ".strm", options.Urls.Build(item.Id)));
                        break;
                    }

                case RemoteItemTypes.MusicAlbum:
                    {
                        if (options.DownloadArtwork && FindImage(item, "Primary") is RemoteImage cover)
                        {
                            var folder = AlbumFolder(item.AlbumArtist ?? item.Artists.FirstOrDefault(), item.Name);
                            files.Add(DesiredFile.ForImage(folder + "/folder" + cover.Extension, new RemoteImageRef(item.Id, cover.Type, cover.Tag)));
                        }

                        break;
                    }
            }
        }

        // Two albums with the same name can produce the same folder.jpg; keep the first.
        return files.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static int TypeOrder(string type) => type switch
    {
        RemoteItemTypes.Series => 0,
        RemoteItemTypes.MusicAlbum => 1,
        _ => 2,
    };

    private static string ResolveSeriesFolder(RemoteItem episode, Dictionary<Guid, string> folders, Dictionary<Guid, RemoteItem> series, HashSet<string> used)
    {
        if (episode.SeriesId is Guid id && folders.TryGetValue(id, out var folder))
        {
            return folder;
        }

        // Series item missing from the catalog: fall back to the name, shared by all its episodes.
        var key = episode.SeriesId ?? Guid.Empty;
        if (!folders.TryGetValue(key, out folder) || key == Guid.Empty)
        {
            folder = FileNameSanitizer.Sanitize(episode.SeriesName, "Unknown Series");
            used.Add(folder);
            if (key != Guid.Empty)
            {
                folders[key] = folder;
            }
        }

        return folder;
    }

    private static string SeriesFolderName(RemoteItem s) => TitleWithYear(s) + ProviderTag(s, "tvdb", "tmdb", "imdb");

    private static string TitleWithYear(RemoteItem item)
        => item.ProductionYear is int year && year > 0
            ? $"{item.Name} ({year.ToString(CultureInfo.InvariantCulture)})"
            : item.Name;

    private static string ProviderTag(RemoteItem item, params string[] preferred)
    {
        foreach (var provider in preferred)
        {
            if (item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id))
            {
                var safe = FileNameSanitizer.Sanitize(id, string.Empty);
                if (safe.Length > 0)
                {
                    return $" [{provider}id-{safe}]";
                }
            }
        }

        return string.Empty;
    }

    private static string EpisodeFileName(RemoteItem e)
    {
        var series = string.IsNullOrWhiteSpace(e.SeriesName) ? "Episode" : e.SeriesName;
        if (e.IndexNumber is not int ep)
        {
            return FileNameSanitizer.Sanitize(series + " - " + e.Name);
        }

        var code = "S" + (e.ParentIndexNumber ?? 1).ToString("00", CultureInfo.InvariantCulture)
            + "E" + ep.ToString("00", CultureInfo.InvariantCulture);
        if (e.IndexNumberEnd is int end && end > ep)
        {
            code += "-E" + end.ToString("00", CultureInfo.InvariantCulture);
        }

        return FileNameSanitizer.Sanitize($"{series} {code} - {e.Name}");
    }

    private static string AlbumFolder(string? artist, string? album)
        => FileNameSanitizer.Sanitize(artist, "Unknown Artist") + "/" + FileNameSanitizer.Sanitize(album, "Unknown Album");

    private static void AddPlayable(List<DesiredFile> files, MirrorOptions options, RemoteItem item, string basePath, string nfo)
    {
        files.Add(DesiredFile.ForText(basePath + ".strm", options.Urls.Build(item.Id)));
        files.Add(DesiredFile.ForText(basePath + ".nfo", nfo));
    }

    private static void AddArtwork(List<DesiredFile> files, MirrorOptions options, RemoteItem item, string posterBase, string backdropBase)
    {
        if (!options.DownloadArtwork)
        {
            return;
        }

        if (FindImage(item, "Primary") is RemoteImage primary)
        {
            files.Add(DesiredFile.ForImage(posterBase + primary.Extension, new RemoteImageRef(item.Id, primary.Type, primary.Tag)));
        }

        if (FindImage(item, "Backdrop") is RemoteImage backdrop)
        {
            files.Add(DesiredFile.ForImage(backdropBase + backdrop.Extension, new RemoteImageRef(item.Id, backdrop.Type, backdrop.Tag)));
        }
    }

    private static RemoteImage? FindImage(RemoteItem item, string type)
    {
        var image = item.Images.FirstOrDefault(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            return null;
        }

        var ext = SafeExtension(image.Extension);
        return ext == image.Extension ? image : new RemoteImage { Type = image.Type, Tag = image.Tag, Extension = ext };
    }

    private static string SafeExtension(string? ext) => ext?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ".jpg",
        ".png" => ".png",
        ".webp" => ".webp",
        ".gif" => ".gif",
        _ => ".jpg",
    };

    /// <summary>Returns <paramref name="name"/> or, if taken, name + " [first 8 of id]".</summary>
    private static string Unique(HashSet<string> used, string name, Guid id, bool isPath = false)
    {
        var candidate = isPath ? name : FileNameSanitizer.Sanitize(name);
        if (used.Add(candidate))
        {
            return candidate;
        }

        var suffixed = candidate + " [" + id.ToString("N")[..8] + "]";
        used.Add(suffixed);
        return suffixed;
    }
}
