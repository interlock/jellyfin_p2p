using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.P2PShare.Core.Model;

namespace Jellyfin.Plugin.P2PShare.Core.Throttling;

/// <summary>
/// Owns the global bucket and one bucket per share, keeping their rates in sync with the
/// current configuration so that edits apply to streams that are already running.
/// </summary>
public sealed class BandwidthManager
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, TokenBucket> _shareBuckets = new();

    public BandwidthManager(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Global = new TokenBucket(time, 0);
    }

    /// <summary>Gets the server-wide bucket.</summary>
    public TokenBucket Global { get; }

    /// <summary>Returns the buckets a stream for <paramref name="share"/> must pass through, with rates refreshed.</summary>
    public IReadOnlyList<TokenBucket> GetBuckets(OutgoingShare share, GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(settings);

        Global.SetRate(Units.MbpsToBytesPerSecond(settings.GlobalMaxBandwidthMbps));
        var bucket = _shareBuckets.GetOrAdd(share.Id, _ => new TokenBucket(_time, 0));
        bucket.SetRate(Units.MbpsToBytesPerSecond(share.MaxBandwidthMbps));
        return new[] { Global, bucket };
    }

    /// <summary>Applies new rates to existing buckets (called after a configuration change).</summary>
    public void ApplySettings(GlobalSettings settings, IEnumerable<OutgoingShare> shares)
    {
        Global.SetRate(Units.MbpsToBytesPerSecond(settings.GlobalMaxBandwidthMbps));
        var known = new HashSet<Guid>();
        foreach (var share in shares)
        {
            known.Add(share.Id);
            if (_shareBuckets.TryGetValue(share.Id, out var bucket))
            {
                bucket.SetRate(Units.MbpsToBytesPerSecond(share.MaxBandwidthMbps));
            }
        }

        foreach (var id in _shareBuckets.Keys)
        {
            if (!known.Contains(id))
            {
                _shareBuckets.TryRemove(id, out _);
            }
        }
    }
}
