using System;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Quotas;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Quotas;

public class QuotaWindowTests
{
    // Wednesday 2026-03-18 14:37:12 UTC
    private static readonly DateTimeOffset Now = new(2026, 3, 18, 14, 37, 12, TimeSpan.Zero);

    [Fact]
    public void Hourly()
    {
        var w = QuotaWindow.For(QuotaInterval.Hourly, Now);
        Assert.Equal(new DateTimeOffset(2026, 3, 18, 14, 0, 0, TimeSpan.Zero), w.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 18, 15, 0, 0, TimeSpan.Zero), w.End);
    }

    [Fact]
    public void Daily()
    {
        var w = QuotaWindow.For(QuotaInterval.Daily, Now);
        Assert.Equal(new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.Zero), w.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 19, 0, 0, 0, TimeSpan.Zero), w.End);
    }

    [Fact]
    public void Weekly_StartsMonday()
    {
        var w = QuotaWindow.For(QuotaInterval.Weekly, Now);
        Assert.Equal(new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero), w.Start);
        Assert.Equal(DayOfWeek.Monday, w.Start.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 3, 23, 0, 0, 0, TimeSpan.Zero), w.End);
    }

    [Fact]
    public void Weekly_OnSunday_BelongsToPreviousMonday()
    {
        var sunday = new DateTimeOffset(2026, 3, 22, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero), QuotaWindow.For(QuotaInterval.Weekly, sunday).Start);
    }

    [Fact]
    public void Monthly_HandlesYearEnd()
    {
        var w = QuotaWindow.For(QuotaInterval.Monthly, new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), w.Start);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), w.End);
    }

    [Fact]
    public void NonUtcInput_IsConvertedToUtc()
    {
        // 2026-03-18 23:30 at UTC-7 is 2026-03-19 06:30 UTC.
        var local = new DateTimeOffset(2026, 3, 18, 23, 30, 0, TimeSpan.FromHours(-7));
        Assert.Equal(new DateTimeOffset(2026, 3, 19, 0, 0, 0, TimeSpan.Zero), QuotaWindow.For(QuotaInterval.Daily, local).Start);
    }

    [Fact]
    public void Contains_IsHalfOpen()
    {
        var w = QuotaWindow.For(QuotaInterval.Hourly, Now);
        Assert.True(w.Contains(w.Start));
        Assert.False(w.Contains(w.End));
    }
}
