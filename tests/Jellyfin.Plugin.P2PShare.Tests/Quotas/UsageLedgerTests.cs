using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Quotas;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Quotas;

public class UsageLedgerTests
{
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 3, 18, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Add_AccumulatesWithinWindow()
    {
        var ledger = new UsageLedger(_clock);
        ledger.Add("k", QuotaInterval.Daily, 100);
        ledger.Add("k", QuotaInterval.Daily, 50);
        Assert.Equal(150, ledger.GetWindowUsage("k", QuotaInterval.Daily));
        Assert.Equal(0, ledger.GetWindowUsage("other", QuotaInterval.Daily));
    }

    [Fact]
    public void Window_ResetsAtBoundary_AndKeepsPrevious()
    {
        var ledger = new UsageLedger(_clock);
        ledger.Add("k", QuotaInterval.Daily, 1000);

        _clock.Advance(TimeSpan.FromHours(10)); // next day 00:00
        Assert.Equal(0, ledger.GetWindowUsage("k", QuotaInterval.Daily));

        var entry = ledger.GetEntry("k", QuotaInterval.Daily);
        Assert.Equal(1000, entry.PreviousWindowBytes);
        Assert.Equal(1000, entry.LifetimeBytes);
    }

    [Fact]
    public void Previous_IsZero_WhenWindowsWereSkipped()
    {
        var ledger = new UsageLedger(_clock);
        ledger.Add("k", QuotaInterval.Hourly, 1000);
        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, ledger.GetEntry("k", QuotaInterval.Hourly).PreviousWindowBytes);
    }

    [Fact]
    public void ChangingInterval_StartsNewWindow()
    {
        var ledger = new UsageLedger(_clock);
        ledger.Add("k", QuotaInterval.Monthly, 500);
        Assert.Equal(0, ledger.GetWindowUsage("k", QuotaInterval.Daily));
        Assert.Equal(500, ledger.GetEntry("k", QuotaInterval.Daily).LifetimeBytes);
    }

    [Fact]
    public void Remaining_And_Exhausted()
    {
        var ledger = new UsageLedger(_clock);
        Assert.Null(ledger.GetRemaining("k", QuotaInterval.Daily, 0));
        Assert.False(ledger.IsExhausted("k", QuotaInterval.Daily, 0));

        ledger.Add("k", QuotaInterval.Daily, 700);
        Assert.Equal(300L, ledger.GetRemaining("k", QuotaInterval.Daily, 1000));
        Assert.False(ledger.IsExhausted("k", QuotaInterval.Daily, 1000));

        ledger.Add("k", QuotaInterval.Daily, 400);
        Assert.Equal(0L, ledger.GetRemaining("k", QuotaInterval.Daily, 1000));
        Assert.True(ledger.IsExhausted("k", QuotaInterval.Daily, 1000));
    }

    [Fact]
    public async Task ConcurrentAdds_AreNotLost()
    {
        var ledger = new UsageLedger(_clock);
        var tasks = new Task[8];
        for (var t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < 10_000; i++)
                {
                    ledger.Add("k", QuotaInterval.Daily, 1);
                }
            });
        }

        await Task.WhenAll(tasks);
        Assert.Equal(80_000, ledger.GetWindowUsage("k", QuotaInterval.Daily));
    }

    [Fact]
    public void Store_SavesAndLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "p2pshare-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UsageLedgerStore(Path.Combine(dir, "usage.json"));
            var ledger = new UsageLedger(_clock);
            ledger.Add(UsageKeys.Global, QuotaInterval.Monthly, 12345);
            Assert.True(ledger.IsDirty);
            Assert.True(store.Save(ledger));
            Assert.False(ledger.IsDirty);
            Assert.False(store.Save(ledger)); // nothing changed

            var reloaded = new UsageLedger(_clock);
            Assert.True(store.LoadInto(reloaded));
            Assert.Equal(12345, reloaded.GetWindowUsage(UsageKeys.Global, QuotaInterval.Monthly));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void Store_CorruptFile_StartsEmpty()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            var ledger = new UsageLedger(_clock);
            Assert.False(new UsageLedgerStore(path).LoadInto(ledger));
            Assert.Empty(ledger.Snapshot());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
