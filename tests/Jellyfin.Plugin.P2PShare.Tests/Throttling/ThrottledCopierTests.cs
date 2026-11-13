using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Throttling;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Throttling;

public class ThrottledCopierTests
{
    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task Copy_Unlimited_CopiesEverythingIntact()
    {
        var data = Data(300_000);
        var dest = new MemoryStream();
        var result = await new ThrottledCopier().CopyAsync(new MemoryStream(data), dest, Array.Empty<TokenBucket>(), null, null, CancellationToken.None);

        Assert.Equal(data.Length, result.BytesCopied);
        Assert.Equal(CopyStopReason.Completed, result.StopReason);
        Assert.Equal(data, dest.ToArray());
    }

    [Fact]
    public async Task Copy_RespectsMaxBytes()
    {
        var dest = new MemoryStream();
        var result = await new ThrottledCopier().CopyAsync(new MemoryStream(Data(100_000)), dest, Array.Empty<TokenBucket>(), 12_345, null, CancellationToken.None);
        Assert.Equal(12_345, result.BytesCopied);
        Assert.Equal(12_345, dest.Length);
    }

    [Fact]
    public async Task Copy_ThrottlesToSlowestBucket_UsingFakeClock()
    {
        var clock = new ManualTimeProvider();
        var fast = new TokenBucket(clock, 1_000_000);
        var slow = new TokenBucket(clock, 50_000);
        var copier = new ThrottledCopier(clock.FakeDelay);
        var start = clock.GetUtcNow();

        var result = await copier.CopyAsync(new MemoryStream(Data(550_000)), Stream.Null, new[] { fast, slow }, null, null, CancellationToken.None);

        // 550 KB at 50 KB/s with a 50 KB burst => 10 s.
        Assert.Equal(550_000, result.BytesCopied);
        Assert.InRange((clock.GetUtcNow() - start).TotalSeconds, 9.9, 10.1);
    }

    [Fact]
    public async Task Copy_StopsWhenMeterSaysSo()
    {
        long metered = 0;
        var result = await new ThrottledCopier().CopyAsync(
            new MemoryStream(Data(1_000_000)),
            Stream.Null,
            Array.Empty<TokenBucket>(),
            null,
            n => (metered += n) < 200_000,
            CancellationToken.None);

        Assert.Equal(CopyStopReason.LimitReached, result.StopReason);
        Assert.Equal(metered, result.BytesCopied);
        Assert.InRange(result.BytesCopied, 200_000, 200_000 + ThrottledCopier.MaxChunk);
    }

    [Fact]
    public async Task Copy_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ThrottledCopier().CopyAsync(new MemoryStream(Data(10)), Stream.Null, Array.Empty<TokenBucket>(), null, null, cts.Token));
    }

    [Theory]
    [InlineData(0L, ThrottledCopier.MaxChunk)]
    [InlineData(10_000L, ThrottledCopier.MinChunk)]
    [InlineData(200_000L, 20_000)]
    [InlineData(100_000_000L, ThrottledCopier.MaxChunk)]
    public void ChunkSize_AdaptsToRate(long rate, int expected)
    {
        var buckets = new[] { new TokenBucket(new ManualTimeProvider(), rate) };
        Assert.Equal(expected, ThrottledCopier.ChooseChunkSize(buckets));
    }

    [Fact]
    public async Task Copy_RealClock_IsApproximatelyRateLimited()
    {
        // 1 MB/s with 1 MB burst; copying 1.5 MB should take ~0.5 s.
        var bucket = new TokenBucket(TimeProvider.System, 1_000_000);
        var sw = Stopwatch.StartNew();
        await new ThrottledCopier().CopyAsync(new MemoryStream(Data(1_500_000)), Stream.Null, new[] { bucket }, null, null, CancellationToken.None);
        sw.Stop();
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.4, 1.5);
    }

    [Fact]
    public void BandwidthManager_AppliesGlobalAndShareRates()
    {
        var manager = new BandwidthManager(new ManualTimeProvider());
        var share = new OutgoingShare { MaxBandwidthMbps = 8 };
        var settings = new GlobalSettings { GlobalMaxBandwidthMbps = 80 };

        var buckets = manager.GetBuckets(share, settings);
        Assert.Equal(10_000_000, buckets[0].BytesPerSecond);
        Assert.Equal(1_000_000, buckets[1].BytesPerSecond);

        share.MaxBandwidthMbps = 0;
        manager.ApplySettings(settings, new[] { share });
        Assert.True(buckets[1].IsUnlimited);

        // Same share gets the same bucket so concurrent streams share the limit.
        Assert.Same(buckets[1], manager.GetBuckets(share, settings)[1]);
    }
}
