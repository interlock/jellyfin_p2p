using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sync;

public sealed class MirrorSynchronizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2pshare-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRemote _remote = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private MirrorSynchronizer Sync() => new((img, ct) => _remote.GetImageAsync(img.ItemId, img.ImageType, ct));

    [Fact]
    public async Task FirstSync_WritesEverything_SecondSyncIsNoop()
    {
        var desired = new List<DesiredFile>
        {
            DesiredFile.ForText("A/a.strm", "http://x/1"),
            DesiredFile.ForText("A/a.nfo", "<movie/>"),
            DesiredFile.ForImage("A/poster.jpg", new RemoteImageRef(Guid.NewGuid(), "Primary", "t1")),
        };

        var first = await Sync().SyncAsync(_root, desired, CancellationToken.None);
        Assert.Equal(3, first.Written);
        Assert.Equal("http://x/1", File.ReadAllText(Path.Combine(_root, "A", "a.strm")));
        Assert.True(File.Exists(Path.Combine(_root, MirrorSynchronizer.ManifestFileName)));

        var second = await Sync().SyncAsync(_root, desired, CancellationToken.None);
        Assert.False(second.Changed);
        Assert.Equal(3, second.Unchanged);
        Assert.Equal(1, _remote.ImageRequests); // artwork not re-downloaded
    }

    [Fact]
    public async Task ChangedContent_IsRewritten_RemovedItems_AreDeleted_AndDirsPruned()
    {
        await Sync().SyncAsync(_root, new[]
        {
            DesiredFile.ForText("A/a.strm", "v1"),
            DesiredFile.ForText("B/b.strm", "v1"),
        }, CancellationToken.None);

        var result = await Sync().SyncAsync(_root, new[] { DesiredFile.ForText("A/a.strm", "v2") }, CancellationToken.None);

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Deleted);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_root, "A", "a.strm")));
        Assert.False(Directory.Exists(Path.Combine(_root, "B")));
    }

    [Fact]
    public async Task UnknownFiles_AreNeverDeleted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        var userFile = Path.Combine(_root, "A", "keep-me.txt");
        File.WriteAllText(userFile, "mine");

        await Sync().SyncAsync(_root, new[] { DesiredFile.ForText("A/a.strm", "x") }, CancellationToken.None);
        await Sync().SyncAsync(_root, Array.Empty<DesiredFile>(), CancellationToken.None);

        Assert.True(File.Exists(userFile));
    }

    [Fact]
    public async Task ManuallyDeletedFile_IsRestored()
    {
        var desired = new[] { DesiredFile.ForText("a.strm", "x") };
        await Sync().SyncAsync(_root, desired, CancellationToken.None);
        File.Delete(Path.Combine(_root, "a.strm"));

        var result = await Sync().SyncAsync(_root, desired, CancellationToken.None);
        Assert.Equal(1, result.Written);
    }

    [Fact]
    public void ResolveSafe_RejectsTraversal()
    {
        Assert.Throws<ArgumentException>(() => MirrorSynchronizer.ResolveSafe(_root, "../outside.txt"));
        Assert.Throws<ArgumentException>(() => MirrorSynchronizer.ResolveSafe(_root, "/etc/passwd"));
        Assert.Throws<ArgumentException>(() => MirrorSynchronizer.ResolveSafe(_root, "a/../../x"));
        Assert.StartsWith(Path.GetFullPath(_root), MirrorSynchronizer.ResolveSafe(_root, "a/b.strm"));
    }

    [Fact]
    public async Task RemoveMirror_DeletesOwnedFilesAndFolder()
    {
        await Sync().SyncAsync(_root, new[] { DesiredFile.ForText("A/a.strm", "x") }, CancellationToken.None);
        MirrorSynchronizer.RemoveMirror(_root);
        Assert.False(Directory.Exists(_root));
    }

    // ---- ConnectionSynchronizer ---------------------------------------------------

    private static IncomingConnection NewConnection() => new() { ProxyKey = "key", RemoteServerName = "old" };

    [Fact]
    public async Task ConnectionSync_MirrorsAllLibraries_WithPaging()
    {
        var lib = new RemoteLibrary { Id = Guid.NewGuid(), Name = "Movies", CollectionType = "movies" };
        _remote.Libraries.Add(lib);
        _remote.Items[lib.Id] = Enumerable.Range(1, 1203).Select(i => FakeRemote.Movie("Movie " + i, 2000)).ToList();

        var connection = NewConnection();
        var outcome = await new ConnectionSynchronizer(_remote).SyncAsync(connection, new GlobalSettings { DownloadArtwork = false }, _root, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Alice", connection.RemoteServerName);
        var mirrored = Assert.Single(connection.Libraries);
        Assert.Equal(1203, mirrored.ItemCount);
        Assert.Equal(3, _remote.PageRequests); // 500 + 500 + 203
        var path = ConnectionSynchronizer.MirrorPath(_root, connection.Id, lib.Id);
        Assert.Equal(1203, Directory.EnumerateFiles(path, "*.strm", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task ConnectionSync_DisabledLibrary_IsRemoved_AndUnsharedLibraryIsFlagged()
    {
        var keep = new RemoteLibrary { Id = Guid.NewGuid(), Name = "Movies" };
        var drop = new RemoteLibrary { Id = Guid.NewGuid(), Name = "Shows" };
        _remote.Libraries.AddRange(new[] { keep, drop });
        _remote.Items[keep.Id] = new List<RemoteItem> { FakeRemote.Movie("A", 2000) };
        _remote.Items[drop.Id] = new List<RemoteItem> { FakeRemote.Movie("B", 2000) };

        var connection = NewConnection();
        var settings = new GlobalSettings { DownloadArtwork = false };
        var sync = new ConnectionSynchronizer(_remote);
        await sync.SyncAsync(connection, settings, _root, null, CancellationToken.None);

        // Admin disables "Movies"; peer stops sharing "Shows".
        connection.Libraries.Single(l => l.RemoteLibraryId == keep.Id).Enabled = false;
        _remote.Libraries.Remove(drop);
        var outcome = await sync.SyncAsync(connection, settings, _root, null, CancellationToken.None);

        Assert.False(Directory.Exists(ConnectionSynchronizer.MirrorPath(_root, connection.Id, keep.Id)));
        Assert.False(Directory.Exists(ConnectionSynchronizer.MirrorPath(_root, connection.Id, drop.Id)));
        Assert.False(connection.Libraries.Single(l => l.RemoteLibraryId == drop.Id).AvailableRemotely);
        Assert.Equal(drop.Id, Assert.Single(outcome.RemovedLibraries).RemoteLibraryId);
    }

    [Fact]
    public async Task ConnectionSync_PeerFailure_LeavesMirrorsUntouched()
    {
        var lib = new RemoteLibrary { Id = Guid.NewGuid(), Name = "Movies" };
        _remote.Libraries.Add(lib);
        _remote.Items[lib.Id] = new List<RemoteItem> { FakeRemote.Movie("A", 2000) };
        var connection = NewConnection();
        var settings = new GlobalSettings { DownloadArtwork = false };
        await new ConnectionSynchronizer(_remote).SyncAsync(connection, settings, _root, null, CancellationToken.None);

        _remote.Failure = new RemoteShareException("revoked", System.Net.HttpStatusCode.Forbidden);
        var outcome = await new ConnectionSynchronizer(_remote).SyncAsync(connection, settings, _root, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.AccessDenied);
        Assert.NotEmpty(Directory.EnumerateFiles(ConnectionSynchronizer.MirrorPath(_root, connection.Id, lib.Id), "*.strm", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("", "{library} ({server})", "Movies (Alice)")]
    [InlineData("", "{server} - {library}", "Alice - Movies")]
    [InlineData("Custom Name", "{library} ({server})", "Custom Name")]
    public void LocalLibraryName_UsesOverrideOrTemplate(string localName, string template, string expected)
    {
        var lib = new MirroredLibrary { RemoteName = "Movies", LocalName = localName };
        var connection = new IncomingConnection { RemoteServerName = "Alice" };
        Assert.Equal(expected, ConnectionSynchronizer.LocalLibraryName(lib, connection, new GlobalSettings { LibraryNameTemplate = template }));
    }
}
