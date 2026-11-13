using System;
using Jellyfin.Plugin.P2PShare.Core.Model;

namespace Jellyfin.Plugin.P2PShare.Core.Quotas;

/// <summary>
/// Calendar-aligned (UTC) quota window.
/// </summary>
public readonly record struct QuotaWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    /// <summary>Returns the window of <paramref name="interval"/> containing <paramref name="now"/>.</summary>
    public static QuotaWindow For(QuotaInterval interval, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        DateTimeOffset start;
        DateTimeOffset end;
        switch (interval)
        {
            case QuotaInterval.Hourly:
                start = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
                end = start.AddHours(1);
                break;
            case QuotaInterval.Daily:
                start = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
                end = start.AddDays(1);
                break;
            case QuotaInterval.Weekly:
                var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
                var sinceMonday = ((int)utc.DayOfWeek + 6) % 7;
                start = day.AddDays(-sinceMonday);
                end = start.AddDays(7);
                break;
            case QuotaInterval.Monthly:
                start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
                end = start.AddMonths(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(interval), interval, null);
        }

        return new QuotaWindow(start, end);
    }
}
