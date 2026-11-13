using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.P2PShare.Core.Model;

namespace Jellyfin.Plugin.P2PShare.Core.Sharing;

/// <summary>Why a stream lease was refused.</summary>
public enum LeaseFailure
{
    None,
    ShareStreamLimit,
    GlobalStreamLimit,
    TranscodeLimit,
}

/// <summary>Information about a running peer stream (for the admin dashboard).</summary>
public sealed record ActiveStreamInfo(Guid LeaseId, Guid ShareId, string ShareName, Guid ItemId, string ItemName, bool IsTranscode, string RemoteAddress, DateTimeOffset StartedAt)
{
    private long _bytes;

    public long BytesSent => Interlocked.Read(ref _bytes);

    internal void AddBytes(long n) => Interlocked.Add(ref _bytes, n);
}

/// <summary>
/// Tracks running peer streams and enforces concurrency limits atomically.
/// </summary>
public sealed class ActiveStreamRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ActiveStreamInfo> _active = new();
    private readonly TimeProvider _time;

    public ActiveStreamRegistry(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Gets a snapshot of the running streams.</summary>
    public IReadOnlyList<ActiveStreamInfo> Snapshot()
    {
        lock (_lock)
        {
            return _active.Values.ToList();
        }
    }

    public int CountForShare(Guid shareId)
    {
        lock (_lock)
        {
            return _active.Values.Count(a => a.ShareId == shareId);
        }
    }

    /// <summary>Tries to start a stream. Dispose the returned lease when the stream ends.</summary>
    public LeaseFailure TryBegin(
        OutgoingShare share,
        GlobalSettings settings,
        bool isTranscode,
        Guid itemId,
        string itemName,
        string remoteAddress,
        out StreamLease? lease)
    {
        lease = null;
        lock (_lock)
        {
            var forShare = 0;
            var transcodes = 0;
            foreach (var a in _active.Values)
            {
                if (a.ShareId == share.Id)
                {
                    forShare++;
                }

                if (a.IsTranscode)
                {
                    transcodes++;
                }
            }

            if (share.MaxConcurrentStreams > 0 && forShare >= share.MaxConcurrentStreams)
            {
                return LeaseFailure.ShareStreamLimit;
            }

            if (settings.GlobalMaxConcurrentStreams > 0 && _active.Count >= settings.GlobalMaxConcurrentStreams)
            {
                return LeaseFailure.GlobalStreamLimit;
            }

            if (isTranscode && settings.MaxConcurrentTranscodes > 0 && transcodes >= settings.MaxConcurrentTranscodes)
            {
                return LeaseFailure.TranscodeLimit;
            }

            var info = new ActiveStreamInfo(Guid.NewGuid(), share.Id, share.Name, itemId, itemName, isTranscode, remoteAddress, _time.GetUtcNow());
            _active[info.LeaseId] = info;
            lease = new StreamLease(this, info);
            return LeaseFailure.None;
        }
    }

    internal void End(Guid leaseId)
    {
        lock (_lock)
        {
            _active.Remove(leaseId);
        }
    }

    public static string Describe(LeaseFailure failure) => failure switch
    {
        LeaseFailure.ShareStreamLimit => "Too many concurrent streams for this share.",
        LeaseFailure.GlobalStreamLimit => "The server is at its limit of concurrent shared streams.",
        LeaseFailure.TranscodeLimit => "The server is at its limit of concurrent shared transcodes.",
        _ => "OK",
    };
}

/// <summary>A running stream; disposing it releases its slot.</summary>
public sealed class StreamLease : IDisposable
{
    private readonly ActiveStreamRegistry _owner;
    private int _disposed;

    internal StreamLease(ActiveStreamRegistry owner, ActiveStreamInfo info)
    {
        _owner = owner;
        Info = info;
    }

    public ActiveStreamInfo Info { get; }

    public void AddBytes(long n) => Info.AddBytes(n);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.End(Info.LeaseId);
        }
    }
}
