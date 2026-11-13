using System;

namespace Jellyfin.Plugin.P2PShare.Core;

/// <summary>
/// Unit conversions between admin-friendly values and bytes. Uses decimal (SI) units,
/// matching how ISPs and Jellyfin's own bitrate settings express them.
/// </summary>
public static class Units
{
    /// <summary>Megabits per second to bytes per second. Non-positive means unlimited (0).</summary>
    public static long MbpsToBytesPerSecond(double mbps)
        => mbps <= 0 ? 0 : (long)Math.Round(mbps * 1_000_000d / 8d);

    /// <summary>Megabits per second to bits per second. Non-positive means unlimited (0).</summary>
    public static long MbpsToBitsPerSecond(double mbps)
        => mbps <= 0 ? 0 : (long)Math.Round(mbps * 1_000_000d);

    /// <summary>Gigabytes to bytes. Non-positive means unlimited (0).</summary>
    public static long GBToBytes(double gb)
        => gb <= 0 ? 0 : (long)Math.Round(gb * 1_000_000_000d);

    /// <summary>Bytes to gigabytes.</summary>
    public static double BytesToGB(long bytes) => bytes / 1_000_000_000d;
}
