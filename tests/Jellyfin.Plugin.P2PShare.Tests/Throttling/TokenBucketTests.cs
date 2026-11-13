using System;
using Jellyfin.Plugin.P2PShare.Core.Throttling;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Throttling;

public class TokenBucketTests
{
    [Fact]
    public void Unlimited_NeverWaits()
    {
        var bucket = new TokenBucket(new ManualTimeProvider(), 0);
        Assert.True(bucket.IsUnlimited);
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(long.MaxValue / 2));
    }

    [Fact]
    public void BurstIsAvailableImmediately()
    {
        var bucket = new TokenBucket(new ManualTimeProvider(), 1000);
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(1000));
    }

    [Fact]
    public void Debt_ProducesProportionalWait()
    {
        var bucket = new TokenBucket(new ManualTimeProvider(), 1000);
        bucket.Reserve(1000); // drain burst
        Assert.Equal(TimeSpan.FromSeconds(0.5), bucket.Reserve(500));
        Assert.Equal(TimeSpan.FromSeconds(1.5), bucket.Reserve(1000)); // debt accumulates
    }

    [Fact]
    public void Refills_OverTime_ButNotBeyondCapacity()
    {
        var clock = new ManualTimeProvider();
        var bucket = new TokenBucket(clock, 1000);
        bucket.Reserve(1000);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(1000));      // capacity is 1 s worth
        Assert.Equal(TimeSpan.FromSeconds(1), bucket.Reserve(1000));
    }

    [Fact]
    public void SustainedRate_MatchesConfiguredRate()
    {
        var clock = new ManualTimeProvider();
        var bucket = new TokenBucket(clock, 100_000);
        var start = clock.GetUtcNow();
        long sent = 0;
        while (sent < 1_000_000)
        {
            clock.Advance(bucket.Reserve(10_000));
            sent += 10_000;
        }

        // 1 MB at 100 KB/s with a 100 KB initial burst ≈ 9 s.
        var elapsed = (clock.GetUtcNow() - start).TotalSeconds;
        Assert.InRange(elapsed, 8.9, 9.1);
    }

    [Fact]
    public void SetRate_ToUnlimited_StopsWaiting()
    {
        var bucket = new TokenBucket(new ManualTimeProvider(), 1000);
        bucket.Reserve(5000);
        bucket.SetRate(0);
        Assert.True(bucket.IsUnlimited);
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(5000));
    }

    [Fact]
    public void SetRate_Increase_ShortensFutureWaits()
    {
        var bucket = new TokenBucket(new ManualTimeProvider(), 1000);
        bucket.Reserve(1000);
        bucket.SetRate(10_000);
        Assert.Equal(TimeSpan.FromSeconds(0.1), bucket.Reserve(1000));
    }
}
