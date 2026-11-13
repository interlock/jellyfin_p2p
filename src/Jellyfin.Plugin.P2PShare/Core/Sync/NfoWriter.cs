using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>
/// Writes Kodi-style NFO files that Jellyfin's NFO reader understands.
/// </summary>
public static class NfoWriter
{
    public static string Movie(RemoteItem item) => Write("movie", item, w => WriteCommon(w, item));

    public static string Video(RemoteItem item) => Write("movie", item, w => WriteCommon(w, item));

    public static string TvShow(RemoteItem series) => Write("tvshow", series, w =>
    {
        WriteCommon(w, series);
        Element(w, "season", "-1");
        Element(w, "episode", "-1");
    });

    public static string Episode(RemoteItem episode) => Write("episodedetails", episode, w =>
    {
        WriteCommon(w, episode);
        Element(w, "showtitle", episode.SeriesName);
        Element(w, "season", episode.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture));
        Element(w, "episode", episode.IndexNumber?.ToString(CultureInfo.InvariantCulture));
        if (episode.IndexNumberEnd.HasValue)
        {
            Element(w, "episodenumberend", episode.IndexNumberEnd.Value.ToString(CultureInfo.InvariantCulture));
        }

        Element(w, "aired", episode.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    });

    public static string MusicVideo(RemoteItem item) => Write("musicvideo", item, w =>
    {
        WriteCommon(w, item);
        foreach (var artist in item.Artists)
        {
            Element(w, "artist", artist);
        }

        Element(w, "album", item.Album);
    });

    private static string Write(string root, RemoteItem item, Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
            CheckCharacters = false,
        };

        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument(true);
            w.WriteStartElement(root);
            body(w);
            Element(w, "lockdata", "false");
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCommon(XmlWriter w, RemoteItem item)
    {
        Element(w, "title", item.Name);
        if (!string.IsNullOrEmpty(item.OriginalTitle) && item.OriginalTitle != item.Name)
        {
            Element(w, "originaltitle", item.OriginalTitle);
        }

        Element(w, "plot", item.Overview);
        Element(w, "year", item.ProductionYear?.ToString(CultureInfo.InvariantCulture));
        Element(w, "premiered", item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Element(w, "mpaa", item.OfficialRating);
        Element(w, "rating", item.CommunityRating?.ToString("0.0", CultureInfo.InvariantCulture));
        if (item.RunTimeTicks is long ticks && ticks > 0)
        {
            Element(w, "runtime", ((long)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes)).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var genre in item.Genres)
        {
            Element(w, "genre", genre);
        }

        foreach (var studio in item.Studios)
        {
            Element(w, "studio", studio);
        }

        var first = true;
        foreach (var (provider, id) in item.ProviderIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            w.WriteStartElement("uniqueid");
            w.WriteAttributeString("type", provider.ToLowerInvariant());
            if (first)
            {
                w.WriteAttributeString("default", "true");
                first = false;
            }

            w.WriteString(Clean(id));
            w.WriteEndElement();
        }

        if (item.ProviderIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            Element(w, "imdbid", imdb);
        }

        if (item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
        {
            Element(w, "tmdbid", tmdb);
        }

        if (item.ProviderIds.TryGetValue("Tvdb", out var tvdb) && !string.IsNullOrWhiteSpace(tvdb))
        {
            Element(w, "tvdbid", tvdb);
        }
    }

    private static void Element(XmlWriter w, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        w.WriteElementString(name, Clean(value));
    }

    /// <summary>Removes characters that are invalid in XML 1.0.</summary>
    private static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (XmlConvert.IsXmlChar(c) || char.IsSurrogate(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
