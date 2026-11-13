namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// Length of a data volume quota window. Windows are calendar aligned in UTC.
/// </summary>
public enum QuotaInterval
{
    /// <summary>Resets at the top of every hour.</summary>
    Hourly = 0,

    /// <summary>Resets every day at 00:00 UTC.</summary>
    Daily = 1,

    /// <summary>Resets every Monday at 00:00 UTC.</summary>
    Weekly = 2,

    /// <summary>Resets on the 1st of every month at 00:00 UTC.</summary>
    Monthly = 3,
}
