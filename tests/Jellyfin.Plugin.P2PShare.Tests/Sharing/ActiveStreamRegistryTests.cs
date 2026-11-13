using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sharing;

public class ActiveStreamRegistryTests
{
    private readonly ActiveStreamRegistry _registry = new(new ManualTimeProvider());
    private readonly GlobalSettings _settings = new() { MaxConcurrentTranscodes = 0 };

    private LeaseFailure Begin(OutgoingShare share, bool transcode, out StreamLease? lease)
        => _registry.TryBegin(share, _settings, transcode, Guid.NewGuid(), "item", "127.0.0.1", out lease);

    [Fact]
    public void ShareLimit_IsEnforced_AndReleasedOnDispose()
    {
        var share = new OutgoingShare { MaxConcurrentStreams = 2 };
        Assert.Equal(LeaseFailure.None, Begin(share, false, out var a));
        Assert.Equal(LeaseFailure.None, Begin(share, false, out var b));
        Assert.Equal(LeaseFailure.ShareStreamLimit, Begin(share, false, out var c));
        Assert.Null(c);

        a!.Dispose();
        a.Dispose(); // idempotent
        Assert.Equal(1, _registry.CountForShare(share.Id));
        Assert.Equal(LeaseFailure.None, Begin(share, false, out _));
        b!.Dispose();
    }

    [Fact]
    public void SharesAreIndependent_GlobalLimitIsShared()
    {
        _settings.GlobalMaxConcurrentStreams = 3;
        var s1 = new OutgoingShare { MaxConcurrentStreams = 2 };
        var s2 = new OutgoingShare { MaxConcurrentStreams = 2 };

        Assert.Equal(LeaseFailure.None, Begin(s1, false, out _));
        Assert.Equal(LeaseFailure.None, Begin(s1, false, out _));
        Assert.Equal(LeaseFailure.None, Begin(s2, false, out _));
        Assert.Equal(LeaseFailure.GlobalStreamLimit, Begin(s2, false, out _));
    }

    [Fact]
    public void TranscodeLimit_OnlyAppliesToTranscodes()
    {
        _settings.MaxConcurrentTranscodes = 1;
        var share = new OutgoingShare { MaxConcurrentStreams = 0 };
        Assert.Equal(LeaseFailure.None, Begin(share, true, out _));
        Assert.Equal(LeaseFailure.TranscodeLimit, Begin(share, true, out _));
        Assert.Equal(LeaseFailure.None, Begin(share, false, out _));
    }

    [Fact]
    public void Snapshot_TracksBytes()
    {
        var share = new OutgoingShare { Name = "Bob" };
        Begin(share, false, out var lease);
        lease!.AddBytes(1234);
        var info = Assert.Single(_registry.Snapshot());
        Assert.Equal("Bob", info.ShareName);
        Assert.Equal(1234, info.BytesSent);
    }

    [Fact]
    public async Task ConcurrentBegin_NeverExceedsLimit()
    {
        var share = new OutgoingShare { MaxConcurrentStreams = 5 };
        var leases = new ConcurrentBag<StreamLease>();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            if (Begin(share, false, out var lease) == LeaseFailure.None)
            {
                leases.Add(lease!);
            }
        })));

        Assert.Equal(5, leases.Count);
    }
}
