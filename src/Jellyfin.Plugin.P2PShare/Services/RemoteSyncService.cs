using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Receiving side: syncs incoming connections into mirror folders and Jellyfin libraries.
/// </summary>
public sealed class RemoteSyncService
{
    private readonly ConfigStore _config;
    private readonly PeerClientFactory _clients;
    private readonly LibraryRegistrar _registrar;
    private readonly ILogger<RemoteSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RemoteSyncService(ConfigStore config, PeerClientFactory clients, LibraryRegistrar registrar, ILogger<RemoteSyncService> logger)
    {
        _config = config;
        _clients = clients;
        _registrar = registrar;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether a sync is running.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Starts syncing one connection in the background.</summary>
    public void QueueSync(Guid connectionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAsync(new[] { connectionId }, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background P2P Share sync failed");
            }
        });
    }

    /// <summary>Syncs all enabled connections.</summary>
    public Task SyncAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        => SyncAsync(_config.Snapshot.Connections.Where(c => c.Enabled).Select(c => c.Id).ToList(), progress, cancellationToken);

    /// <summary>Syncs the given connections, one at a time.</summary>
    public async Task SyncAsync(IReadOnlyList<Guid> connectionIds, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var anyChanged = false;
            for (var i = 0; i < connectionIds.Count; i++)
            {
                var index = i;
                var inner = progress is null ? null : new Progress<double>(p => progress.Report(((index * 100.0) + p) / connectionIds.Count));
                anyChanged |= await SyncOneAsync(connectionIds[i], inner, cancellationToken).ConfigureAwait(false);
            }

            if (anyChanged)
            {
                _registrar.QueueScan();
            }

            progress?.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a connection's libraries and mirror folders (used when deleting a connection).</summary>
    public async Task RemoveConnectionArtifactsAsync(IncomingConnection connection)
    {
        var root = _config.MirrorRoot;
        foreach (var lib in connection.Libraries)
        {
            var path = ConnectionSynchronizer.MirrorPath(root, connection.Id, lib.RemoteLibraryId);
            await _registrar.RemoveAsync(path).ConfigureAwait(false);
            MirrorSynchronizer.RemoveMirror(path);
        }

        var connectionDir = System.IO.Path.Combine(root, connection.Id.ToString("N"));
        if (System.IO.Directory.Exists(connectionDir) && !System.IO.Directory.EnumerateFileSystemEntries(connectionDir).Any())
        {
            System.IO.Directory.Delete(connectionDir);
        }
    }

    private async Task<bool> SyncOneAsync(Guid connectionId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var snapshot = _config.Snapshot;
        var shared = snapshot.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (shared is null || !shared.Enabled)
        {
            return false;
        }

        // The synchronizer mutates the connection; never mutate the shared snapshot.
        var connection = System.Text.Json.JsonSerializer.Deserialize<IncomingConnection>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(shared))!;

        var settings = snapshot.Global;
        var mirrorRoot = _config.MirrorRoot;
        ConnectionSyncOutcome outcome;
        try
        {
            var client = _clients.Create(connection, settings);
            outcome = await new ConnectionSynchronizer(client).SyncAsync(connection, settings, mirrorRoot, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            outcome = new ConnectionSyncOutcome(null, Array.Empty<LibrarySyncOutcome>(), Array.Empty<MirroredLibrary>(), ex.Message, false);
        }

        var changed = false;
        foreach (var removed in outcome.RemovedLibraries)
        {
            changed |= await _registrar.RemoveAsync(ConnectionSynchronizer.MirrorPath(mirrorRoot, connection.Id, removed.RemoteLibraryId)).ConfigureAwait(false);
        }

        foreach (var lib in connection.Libraries.Where(l => !l.Enabled || !l.AvailableRemotely))
        {
            changed |= await _registrar.RemoveAsync(ConnectionSynchronizer.MirrorPath(mirrorRoot, connection.Id, lib.RemoteLibraryId)).ConfigureAwait(false);
        }

        foreach (var lib in outcome.Libraries.Where(l => l.Result is not null))
        {
            var name = ConnectionSynchronizer.LocalLibraryName(lib.Library, connection, settings);
            changed |= await _registrar.EnsureAsync(name, lib.Library.CollectionType, lib.MirrorPath).ConfigureAwait(false);
            changed |= lib.Result!.Changed;
        }

        var status = Describe(outcome);
        if (outcome.Succeeded)
        {
            _logger.LogInformation("P2P Share sync of {Server}: {Status}", connection.RemoteServerName, status);
        }
        else
        {
            _logger.LogWarning("P2P Share sync of {Server} had problems: {Status}", connection.RemoteServerName, status);
        }

        // Persist the merged libraries and status onto the live configuration.
        _config.Update(c =>
        {
            var live = c.Connections.FirstOrDefault(x => x.Id == connectionId);
            if (live is null)
            {
                return; // Deleted while syncing.
            }

            live.RemoteServerName = connection.RemoteServerName;
            live.Libraries = MergeAdminEdits(live.Libraries, connection.Libraries);
            live.LastSyncAt = DateTime.UtcNow;
            live.LastSyncStatus = status;
            live.LastSyncFailed = !outcome.Succeeded;
        });

        return changed;
    }

    /// <summary>Keeps admin choices (Enabled/LocalName) made while the sync ran, and sync results otherwise.</summary>
    private static List<MirroredLibrary> MergeAdminEdits(List<MirroredLibrary> live, List<MirroredLibrary> synced)
    {
        foreach (var s in synced)
        {
            var l = live.FirstOrDefault(x => x.RemoteLibraryId == s.RemoteLibraryId);
            if (l is not null)
            {
                s.Enabled = l.Enabled;
                s.LocalName = l.LocalName;
            }
        }

        return synced;
    }

    private static string Describe(ConnectionSyncOutcome outcome)
    {
        if (outcome.Error is not null)
        {
            return outcome.AccessDenied ? "Access denied by peer: " + outcome.Error : outcome.Error;
        }

        var parts = outcome.Libraries.Select(l => l.Error is not null
            ? $"{l.Library.RemoteName}: {l.Error}"
            : $"{l.Library.RemoteName}: {l.Library.ItemCount} items (+{l.Result!.Written} / -{l.Result.Deleted})");
        var text = string.Join("; ", parts);
        return string.IsNullOrEmpty(text) ? "No libraries shared." : text;
    }
}
