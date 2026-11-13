using System;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Quotas;

namespace Jellyfin.Plugin.P2PShare.Core.Sharing;

/// <summary>How a stream will be delivered.</summary>
public enum StreamMode
{
    Deny,
    Direct,
    Transcode,
}

/// <summary>Why a stream was denied.</summary>
public enum DenyReason
{
    None,
    ShareQuotaExceeded,
    GlobalQuotaExceeded,
    TranscodingNotAllowed,
    BitrateExceedsLimit,
}

/// <summary>A peer's stream request.</summary>
/// <param name="WantsTranscode">The peer asked for a transcode.</param>
/// <param name="RequestedMaxBitrate">Requested bitrate in bits/s, or 0.</param>
/// <param name="ItemBitrate">The source media bitrate in bits/s, or 0 if unknown.</param>
public readonly record struct StreamRequest(bool WantsTranscode, long RequestedMaxBitrate, long ItemBitrate);

/// <summary>The decision for a stream request.</summary>
public sealed record StreamDecision(StreamMode Mode, long TargetBitrate, DenyReason Reason)
{
    public bool Allowed => Mode != StreamMode.Deny;

    public int StatusCode => Reason switch
    {
        DenyReason.None => 200,
        DenyReason.ShareQuotaExceeded or DenyReason.GlobalQuotaExceeded => 429,
        _ => 403,
    };

    public string Message => Reason switch
    {
        DenyReason.None => "OK",
        DenyReason.ShareQuotaExceeded => "The data quota for this share has been used up for the current period.",
        DenyReason.GlobalQuotaExceeded => "The server's sharing data quota has been used up for the current period.",
        DenyReason.TranscodingNotAllowed => "Transcoding is not allowed for this share.",
        DenyReason.BitrateExceedsLimit => "The item's bitrate exceeds this share's limit and transcoding is not allowed.",
        _ => "Denied.",
    };

    public static StreamDecision Direct() => new(StreamMode.Direct, 0, DenyReason.None);

    public static StreamDecision Transcode(long bitrate) => new(StreamMode.Transcode, bitrate, DenyReason.None);

    public static StreamDecision Deny(DenyReason reason) => new(StreamMode.Deny, 0, reason);
}

/// <summary>
/// Decides whether and how a stream is served. Concurrency is enforced separately
/// by <see cref="ActiveStreamRegistry"/>.
/// </summary>
public static class StreamPolicy
{
    /// <summary>Bitrate used when a transcode is requested without any bitrate hint (bits/s).</summary>
    public const long DefaultTranscodeBitrate = 8_000_000;

    /// <summary>Lowest bitrate we will transcode to (bits/s).</summary>
    public const long MinTranscodeBitrate = 500_000;

    public static StreamDecision Decide(StreamRequest request, OutgoingShare share, GlobalSettings settings, UsageLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ledger);

        if (ledger.IsExhausted(share.UsageKey, share.QuotaInterval, Units.GBToBytes(share.QuotaGB)))
        {
            return StreamDecision.Deny(DenyReason.ShareQuotaExceeded);
        }

        if (ledger.IsExhausted(UsageKeys.Global, settings.GlobalQuotaInterval, Units.GBToBytes(settings.GlobalQuotaGB)))
        {
            return StreamDecision.Deny(DenyReason.GlobalQuotaExceeded);
        }

        var canTranscode = settings.AllowTranscoding && share.AllowTranscoding;
        var cap = Units.MbpsToBitsPerSecond(share.MaxStreamingBitrateMbps);

        if (request.WantsTranscode)
        {
            if (!canTranscode)
            {
                return StreamDecision.Deny(DenyReason.TranscodingNotAllowed);
            }

            var target = request.RequestedMaxBitrate > 0 ? request.RequestedMaxBitrate : (cap > 0 ? cap : DefaultTranscodeBitrate);
            if (cap > 0)
            {
                target = Math.Min(target, cap);
            }

            return StreamDecision.Transcode(Math.Max(MinTranscodeBitrate, target));
        }

        if (cap > 0 && request.ItemBitrate > cap)
        {
            return canTranscode
                ? StreamDecision.Transcode(Math.Max(MinTranscodeBitrate, cap))
                : StreamDecision.Deny(DenyReason.BitrateExceedsLimit);
        }

        return StreamDecision.Direct();
    }
}
