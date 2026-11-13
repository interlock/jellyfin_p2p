using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.P2PShare.Tasks;

/// <summary>
/// Scheduled task that refreshes all libraries received from peers.
/// </summary>
public class SyncRemoteLibrariesTask : IScheduledTask
{
    private readonly RemoteSyncService _sync;
    private readonly ConfigStore _config;

    public SyncRemoteLibrariesTask(RemoteSyncService sync, ConfigStore config)
    {
        _sync = sync;
        _config = config;
    }

    public string Name => "Sync shared libraries from peers";

    public string Key => "P2PShareSyncRemoteLibraries";

    public string Description => "Refreshes libraries shared with this server through P2P Share invite tokens.";

    public string Category => "P2P Share";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _sync.SyncAllAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Math.Max(1, _config.Snapshot.Global.SyncIntervalHours);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks,
        };
    }
}
