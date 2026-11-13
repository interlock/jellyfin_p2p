using System;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Quotas;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sharing;

public class StreamPolicyTests
{
    private readonly UsageLedger _ledger = new(new ManualTimeProvider());
    private readonly GlobalSettings _settings = new();
    private readonly OutgoingShare _share = new();

    private StreamDecision Decide(bool transcode = false, long requested = 0, long itemBitrate = 5_000_000)
        => StreamPolicy.Decide(new StreamRequest(transcode, requested, itemBitrate), _share, _settings, _ledger);

    [Fact]
    public void NoLimits_DirectPlay()
    {
        var d = Decide();
        Assert.Equal(StreamMode.Direct, d.Mode);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void ShareQuotaExhausted_429()
    {
        _share.QuotaGB = 1;
        _ledger.Add(_share.UsageKey, _share.QuotaInterval, 1_000_000_000);
        var d = Decide();
        Assert.Equal(DenyReason.ShareQuotaExceeded, d.Reason);
        Assert.Equal(429, d.StatusCode);
    }

    [Fact]
    public void GlobalQuotaExhausted_429()
    {
        _settings.GlobalQuotaGB = 0.5;
        _ledger.Add(UsageKeys.Global, _settings.GlobalQuotaInterval, 600_000_000);
        Assert.Equal(DenyReason.GlobalQuotaExceeded, Decide().Reason);
    }

    [Fact]
    public void TranscodeRequested_ShareDisallows_403()
    {
        _share.AllowTranscoding = false;
        var d = Decide(transcode: true);
        Assert.Equal(DenyReason.TranscodingNotAllowed, d.Reason);
        Assert.Equal(403, d.StatusCode);
    }

    [Fact]
    public void TranscodeRequested_GlobalSwitchOverridesShare()
    {
        _share.AllowTranscoding = true;
        _settings.AllowTranscoding = false;
        Assert.Equal(DenyReason.TranscodingNotAllowed, Decide(transcode: true).Reason);
    }

    [Fact]
    public void TranscodeRequested_DefaultsAndCaps()
    {
        _share.AllowTranscoding = true;
        Assert.Equal(StreamPolicy.DefaultTranscodeBitrate, Decide(transcode: true).TargetBitrate);

        _share.MaxStreamingBitrateMbps = 4;
        Assert.Equal(4_000_000, Decide(transcode: true).TargetBitrate);
        Assert.Equal(4_000_000, Decide(transcode: true, requested: 20_000_000).TargetBitrate);
        Assert.Equal(2_000_000, Decide(transcode: true, requested: 2_000_000).TargetBitrate);
        Assert.Equal(StreamPolicy.MinTranscodeBitrate, Decide(transcode: true, requested: 1000).TargetBitrate);
    }

    [Fact]
    public void Direct_OverCap_TranscodesWhenAllowed()
    {
        _share.MaxStreamingBitrateMbps = 10;
        _share.AllowTranscoding = true;
        var d = Decide(itemBitrate: 40_000_000);
        Assert.Equal(StreamMode.Transcode, d.Mode);
        Assert.Equal(10_000_000, d.TargetBitrate);
    }

    [Fact]
    public void Direct_OverCap_DeniedWhenTranscodingNotAllowed()
    {
        _share.MaxStreamingBitrateMbps = 10;
        Assert.Equal(DenyReason.BitrateExceedsLimit, Decide(itemBitrate: 40_000_000).Reason);
    }

    [Fact]
    public void Direct_UnderCap_OrUnknownBitrate_IsDirect()
    {
        _share.MaxStreamingBitrateMbps = 10;
        Assert.Equal(StreamMode.Direct, Decide(itemBitrate: 9_000_000).Mode);
        Assert.Equal(StreamMode.Direct, Decide(itemBitrate: 0).Mode);
    }

    [Fact]
    public void QuotaMeter_StopsStreamAtShareQuota()
    {
        _share.QuotaGB = 0.000_1; // 100 KB
        var meter = new QuotaMeter(_ledger, _share, _settings);
        Assert.True(meter.OnChunk(60_000));
        Assert.False(meter.OnChunk(60_000));
        Assert.Equal(DenyReason.ShareQuotaExceeded, meter.StoppedBecause);
        Assert.Equal(120_000, _ledger.GetWindowUsage(UsageKeys.Global, _settings.GlobalQuotaInterval));
    }

    [Fact]
    public void QuotaMeter_StopsStreamAtGlobalQuota()
    {
        _settings.GlobalQuotaGB = 0.000_05; // 50 KB
        var meter = new QuotaMeter(_ledger, _share, _settings);
        Assert.False(meter.OnChunk(64_000));
        Assert.Equal(DenyReason.GlobalQuotaExceeded, meter.StoppedBecause);
    }
}
