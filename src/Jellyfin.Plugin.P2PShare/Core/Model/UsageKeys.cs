using System;

namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// Keys used in the usage ledger.
/// </summary>
public static class UsageKeys
{
    /// <summary>Key for server-wide usage.</summary>
    public const string Global = "global";

    /// <summary>Key for a share's usage.</summary>
    public static string ForShare(Guid shareId) => "share:" + shareId.ToString("N");
}
