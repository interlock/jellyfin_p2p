using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>Outcome of syncing one mirrored library.</summary>
public sealed record LibrarySyncOutcome(MirroredLibrary Library, string MirrorPath, SyncResult? Result, string? Error);

/// <summary>Outcome of syncing a connection.</summary>
public sealed record ConnectionSyncOutcome(
    RemoteServerInfo? ServerInfo,
    IReadOnlyList<LibrarySyncOutcome> Libraries,
    IReadOnlyList<MirroredLibrary> RemovedLibraries,
    string? Error,
    bool AccessDenied)
{
    public bool Succeeded => Error is null && Libraries.All(l => l.Error is null);
}

/// <summary>
/// Syncs all mirrored libraries of one connection. Pure orchestration: it mutates the
/// <see cref="IncomingConnection"/> it is given and writes mirror folders; registering
/// Jellyfin libraries and saving configuration are the caller's job.
/// </summary>
public sealed class ConnectionSynchronizer
{
    private readonly IRemoteShareClient _client;

    public ConnectionSynchronizer(IRemoteShareClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Folder of a mirrored library under the mirror root.</summary>
    public static string MirrorPath(string mirrorRoot, Guid connectionId, Guid remoteLibraryId)
        => Path.Combine(mirrorRoot, connectionId.ToString("N"), remoteLibraryId.ToString("N"));

    /// <summary>
    /// Merges the peer's current library list into the connection. New libraries are added
    /// (enabled), libraries no longer offered are marked unavailable.
    /// </summary>
    /// <returns>Libraries that disappeared remotely in this merge.</returns>
    public static List<MirroredLibrary> MergeLibraries(IncomingConnection connection, IReadOnlyList<RemoteLibrary> remote)
    {
        var removed = new List<MirroredLibrary>();
        var remoteById = remote.ToDictionary(r => r.Id);

        foreach (var local in connection.Libraries)
        {
            if (remoteById.TryGetValue(local.RemoteLibraryId, out var r))
            {
                local.RemoteName = r.Name;
                local.CollectionType = r.CollectionType;
                local.AvailableRemotely = true;
            }
            else if (local.AvailableRemotely)
            {
                local.AvailableRemotely = false;
                removed.Add(local);
            }
        }

        foreach (var r in remote)
        {
            if (connection.Libraries.All(l => l.RemoteLibraryId != r.Id))
            {
                connection.Libraries.Add(new MirroredLibrary
                {
                    RemoteLibraryId = r.Id,
                    RemoteName = r.Name,
                    CollectionType = r.CollectionType,
                    Enabled = true,
                });
            }
        }

        return removed;
    }

    /// <summary>Resolves the local library name from the template.</summary>
    public static string LocalLibraryName(MirroredLibrary library, IncomingConnection connection, GlobalSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(library.LocalName))
        {
            return library.LocalName.Trim();
        }

        var template = string.IsNullOrWhiteSpace(settings.LibraryNameTemplate) ? "{library} ({server})" : settings.LibraryNameTemplate;
        var server = string.IsNullOrWhiteSpace(connection.RemoteServerName) ? "Remote" : connection.RemoteServerName;
        return template.Replace("{library}", library.RemoteName, StringComparison.OrdinalIgnoreCase)
            .Replace("{server}", server, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public async Task<ConnectionSyncOutcome> SyncAsync(
        IncomingConnection connection,
        GlobalSettings settings,
        string mirrorRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        RemoteServerInfo info;
        IReadOnlyList<RemoteLibrary> remoteLibraries;
        try
        {
            info = await _client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
            remoteLibraries = await _client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteShareException ex)
        {
            // Leave mirrors untouched on failure: no mass deletion on a transient error.
            return new ConnectionSyncOutcome(null, Array.Empty<LibrarySyncOutcome>(), Array.Empty<MirroredLibrary>(), ex.Message, ex.IsAccessDenied);
        }

        if (!string.IsNullOrWhiteSpace(info.ServerName))
        {
            connection.RemoteServerName = info.ServerName;
        }

        var removed = MergeLibraries(connection, remoteLibraries);
        foreach (var lib in removed)
        {
            MirrorSynchronizer.RemoveMirror(MirrorPath(mirrorRoot, connection.Id, lib.RemoteLibraryId));
        }

        var urls = new ProxyUrlBuilder(
            settings.LocalProxyBaseUrl,
            connection.Id,
            connection.ProxyKey,
            connection.PreferTranscode,
            Units.MbpsToBitsPerSecond(connection.PreferredMaxBitrateMbps));
        var options = new MirrorOptions(urls, settings.DownloadArtwork);
        var synchronizer = new MirrorSynchronizer((img, ct) => _client.GetImageAsync(img.ItemId, img.ImageType, ct));

        var outcomes = new List<LibrarySyncOutcome>();
        var active = connection.Libraries.Where(l => l.AvailableRemotely).ToList();
        for (var i = 0; i < active.Count; i++)
        {
            var lib = active[i];
            var path = MirrorPath(mirrorRoot, connection.Id, lib.RemoteLibraryId);
            if (!lib.Enabled)
            {
                MirrorSynchronizer.RemoveMirror(path);
                continue;
            }

            try
            {
                var items = await RemoteShareClient.GetAllItemsAsync(_client, lib.RemoteLibraryId, cancellationToken).ConfigureAwait(false);
                var desired = MirrorPlanner.Plan(items, options);
                var result = await synchronizer.SyncAsync(path, desired, cancellationToken).ConfigureAwait(false);
                lib.ItemCount = items.Count(it => it.IsPlayable);
                outcomes.Add(new LibrarySyncOutcome(lib, path, result, result.Failed > 0 ? $"{result.Failed} file(s) failed" : null));
            }
            catch (RemoteShareException ex)
            {
                outcomes.Add(new LibrarySyncOutcome(lib, path, null, ex.Message));
            }

            progress?.Report(100.0 * (i + 1) / active.Count);
        }

        return new ConnectionSyncOutcome(info, outcomes, removed, null, false);
    }
}
