using System.Linq;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sharing;

public class ByteRangeTests
{
    private const long Total = 1000;

    [Theory]
    [InlineData("bytes=0-99", 0L, 99L)]
    [InlineData("bytes=100-", 100L, 999L)]
    [InlineData("bytes=-200", 800L, 999L)]
    [InlineData("bytes=-5000", 0L, 999L)]
    [InlineData("bytes=900-5000", 900L, 999L)]
    [InlineData("BYTES= 10 - 20 ", 10L, 20L)]
    public void Satisfiable(string header, long start, long end)
    {
        Assert.Equal(RangeParseResult.Satisfiable, ByteRange.TryParse(header, Total, out var range));
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.Equal(end - start + 1, range.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=50-10")]
    [InlineData("bytes=5")]
    public void IgnoredHeaders_ServeFullEntity(string? header)
    {
        Assert.Equal(RangeParseResult.None, ByteRange.TryParse(header, Total, out _));
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=5000-6000")]
    [InlineData("bytes=-0")]
    public void NotSatisfiable(string header)
    {
        Assert.Equal(RangeParseResult.NotSatisfiable, ByteRange.TryParse(header, Total, out _));
    }

    [Fact]
    public void ContentRange_Format()
    {
        Assert.Equal("bytes 0-99/1000", new ByteRange(0, 99).ToContentRange(1000));
    }

    [Fact]
    public void TranscodeArguments_Video()
    {
        var args = TranscodeArguments.Build("/media/movie.mkv", 4_000_000, 0, false);
        var joined = string.Join(' ', args);

        Assert.Equal("/media/movie.mkv", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Contains("-c:v libx264", joined);
        Assert.Contains("-b:v 3808k", joined); // 4000k minus 192k audio
        Assert.Contains("-f mpegts pipe:1", joined);
        Assert.DoesNotContain("-ss", joined);
    }

    [Fact]
    public void TranscodeArguments_SeekAndAudio()
    {
        var args = TranscodeArguments.Build("/m/a.flac", 10_000_000, 905_000_000, true);
        var list = args.ToList();
        Assert.Equal("90.5", list[list.IndexOf("-ss") + 1]);
        Assert.True(list.IndexOf("-ss") < list.IndexOf("-i")); // input seeking
        Assert.Contains("320k", list); // clamped
        Assert.Contains("adts", list);
    }
}
