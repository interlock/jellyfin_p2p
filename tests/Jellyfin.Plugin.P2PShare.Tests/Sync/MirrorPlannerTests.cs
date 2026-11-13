using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sync;

public class MirrorPlannerTests
{
    private static readonly Guid ConnectionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static MirrorOptions Options(bool artwork = true, bool transcode = false)
        => new(new ProxyUrlBuilder("http://127.0.0.1:8096/", ConnectionId, "k3y", transcode, transcode ? 4_000_000 : 0), artwork);

    private static Dictionary<string, DesiredFile> PlanByPath(IReadOnlyCollection<RemoteItem> items, MirrorOptions? options = null)
        => MirrorPlanner.Plan(items, options ?? Options()).ToDictionary(f => f.RelativePath);

    [Fact]
    public void Movie_GetsFolderStrmNfoAndPoster()
    {
        var movie = FakeRemote.Movie("Heat", 1995, tmdb: "949");
        var files = PlanByPath(new[] { movie });

        var strm = files["Heat (1995) [tmdbid-949]/Heat (1995).strm"];
        Assert.Equal($"http://127.0.0.1:8096/P2PShare/Proxy/{ConnectionId:N}/{movie.Id:N}?key=k3y", strm.Text);
        Assert.True(files.ContainsKey("Heat (1995) [tmdbid-949]/Heat (1995).nfo"));
        Assert.Equal(movie.Id, files["Heat (1995) [tmdbid-949]/poster.jpg"].Image!.ItemId);
    }

    [Fact]
    public void Movie_Nfo_IsValidXmlWithProviderIds()
    {
        var movie = FakeRemote.Movie("Amélie & <Friends>", 2001, tmdb: "194");
        movie.Overview = "Line1\nLine2 \u0001 control";
        movie.Genres.Add("Comedy");
        movie.RunTimeTicks = TimeSpan.FromMinutes(122).Ticks;

        var nfo = MirrorPlanner.Plan(new[] { movie }, Options()).Single(f => f.RelativePath.EndsWith(".nfo", StringComparison.Ordinal)).Text!;
        var doc = XDocument.Parse(nfo);

        Assert.Equal("movie", doc.Root!.Name.LocalName);
        Assert.Equal("Amélie & <Friends>", doc.Root.Element("title")!.Value);
        Assert.Equal("2001", doc.Root.Element("year")!.Value);
        Assert.Equal("122", doc.Root.Element("runtime")!.Value);
        Assert.Equal("Comedy", doc.Root.Element("genre")!.Value);
        Assert.Equal("194", doc.Root.Elements("uniqueid").Single(e => (string?)e.Attribute("type") == "tmdb").Value);
    }

    [Fact]
    public void Series_ProducesShowFolderSeasonsAndEpisodes()
    {
        var (series, episodes) = FakeRemote.Series("The Wire", 2002, 2, 2);
        var items = new List<RemoteItem>(episodes) { series };
        var files = PlanByPath(items);

        Assert.True(files.ContainsKey("The Wire (2002) [tvdbid-1234]/tvshow.nfo"));
        Assert.True(files.ContainsKey("The Wire (2002) [tvdbid-1234]/Season 01/The Wire S01E01 - Ep 1.strm"));
        Assert.True(files.ContainsKey("The Wire (2002) [tvdbid-1234]/Season 02/The Wire S02E02 - Ep 2.nfo"));

        var nfo = XDocument.Parse(files["The Wire (2002) [tvdbid-1234]/Season 02/The Wire S02E02 - Ep 2.nfo"].Text!);
        Assert.Equal("2", nfo.Root!.Element("season")!.Value);
        Assert.Equal("2", nfo.Root.Element("episode")!.Value);
    }

    [Fact]
    public void Episode_WithoutSeriesItem_UsesSeriesName()
    {
        var (_, episodes) = FakeRemote.Series("Orphan Show", 2010, 1, 1);
        var files = PlanByPath(episodes);
        Assert.True(files.ContainsKey("Orphan Show/Season 01/Orphan Show S01E01 - Ep 1.strm"));
    }

    [Fact]
    public void MultiEpisodeFile_UsesRangeCode()
    {
        var (series, episodes) = FakeRemote.Series("Show", 2000, 1, 1);
        episodes[0].IndexNumberEnd = 2;
        var files = PlanByPath(new List<RemoteItem>(episodes) { series });
        Assert.Contains(files.Keys, k => k.EndsWith("Show S01E01-E02 - Ep 1.strm", StringComparison.Ordinal));
    }

    [Fact]
    public void Collisions_AreDisambiguatedDeterministically()
    {
        var a = FakeRemote.Movie("Dune", 2021);
        var b = FakeRemote.Movie("Dune", 2021);
        var first = MirrorPlanner.Plan(new[] { a, b }, Options()).Select(f => f.RelativePath).OrderBy(p => p).ToList();
        var second = MirrorPlanner.Plan(new[] { b, a }, Options()).Select(f => f.RelativePath).OrderBy(p => p).ToList();

        Assert.Equal(first, second);
        Assert.Equal(2, first.Count(p => p.EndsWith(".strm", StringComparison.Ordinal)));
        Assert.Contains(first, p => p.Contains(" [", StringComparison.Ordinal) && p.StartsWith("Dune (2021) [", StringComparison.Ordinal));
    }

    [Fact]
    public void Audio_UsesArtistAlbumTrackLayout_AndAlbumCover()
    {
        var album = new RemoteItem { Id = Guid.NewGuid(), Type = RemoteItemTypes.MusicAlbum, Name = "Kid A", AlbumArtist = "Radiohead" };
        album.Images.Add(new RemoteImage { Type = "Primary", Tag = "t", Extension = ".PNG" });
        var track = new RemoteItem { Id = Guid.NewGuid(), Type = RemoteItemTypes.Audio, Name = "Idioteque", Album = "Kid A", AlbumArtist = "Radiohead", IndexNumber = 8 };

        var files = PlanByPath(new[] { album, track });
        Assert.True(files.ContainsKey("Radiohead/Kid A/08 - Idioteque.strm"));
        Assert.True(files.ContainsKey("Radiohead/Kid A/folder.png"));
    }

    [Fact]
    public void ArtworkDisabled_ProducesNoImages()
    {
        var files = MirrorPlanner.Plan(new[] { FakeRemote.Movie("Heat", 1995) }, Options(artwork: false));
        Assert.All(files, f => Assert.NotNull(f.Text));
    }

    [Fact]
    public void Transcode_PreferenceIsInStrmUrl()
    {
        var movie = FakeRemote.Movie("Heat", 1995);
        var strm = MirrorPlanner.Plan(new[] { movie }, Options(transcode: true)).Single(f => f.RelativePath.EndsWith(".strm", StringComparison.Ordinal));
        Assert.EndsWith("?key=k3y&transcode=true&maxBitrate=4000000", strm.Text);
    }

    [Fact]
    public void HostileNames_CannotEscapeFolder()
    {
        var movie = FakeRemote.Movie("../../etc/passwd", 0);
        var movie2 = FakeRemote.Movie("..", 0);
        foreach (var f in MirrorPlanner.Plan(new[] { movie, movie2 }, Options()))
        {
            Assert.DoesNotContain("..", f.RelativePath.Split('/'));
            Assert.False(f.RelativePath.StartsWith('/'));
        }
    }

    [Theory]
    [InlineData("Normal Name", "Normal Name")]
    [InlineData("What? Why: Because*", "What Why- Because")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("CON", "CON_")]
    [InlineData("", "Untitled")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("...hidden", "hidden")]
    public void Sanitizer(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitizer_TruncatesLongNames()
    {
        Assert.Equal(FileNameSanitizer.MaxLength, FileNameSanitizer.Sanitize(new string('x', 500)).Length);
    }
}
