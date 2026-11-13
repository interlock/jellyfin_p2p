using System;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Quotas;

namespace Jellyfin.Plugin.P2PShare.Core.Sharing;

/// <summary>
/// Meters a stream's bytes against the share and global quotas. Use <see cref="OnChunk"/>
/// as the <c>onChunk</c> callback of the throttled copier.
/// </summary>
public sealed class QuotaMeter
{
    private readonly UsageLedger _ledger;
    private readonly OutgoingShare _share;
    private readonly GlobalSettings _settings;
    private readonly StreamLease? _lease;

    public QuotaMeter(UsageLedger ledger, OutgoingShare share, GlobalSettings settings, StreamLease? lease = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _share = share ?? throw new ArgumentNullException(nameof(share));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lease = lease;
    }

    /// <summary>Gets the reason metering stopped the stream, if any.</summary>
    public DenyReason StoppedBecause { get; private set; }

    /// <summary>Records <paramref name="bytes"/>; returns false once a quota is used up.</summary>
    public bool OnChunk(int bytes)
    {
        _ledger.Add(_share.UsageKey, _share.QuotaInterval, bytes);
        _ledger.Add(UsageKeys.Global, _settings.GlobalQuotaInterval, bytes);
        _lease?.AddBytes(bytes);

        if (_ledger.IsExhausted(_share.UsageKey, _share.QuotaInterval, Units.GBToBytes(_share.QuotaGB)))
        {
            StoppedBecause = DenyReason.ShareQuotaExceeded;
            return false;
        }

        if (_ledger.IsExhausted(UsageKeys.Global, _settings.GlobalQuotaInterval, Units.GBToBytes(_settings.GlobalQuotaGB)))
        {
            StoppedBecause = DenyReason.GlobalQuotaExceeded;
            return false;
        }

        return true;
    }
}
