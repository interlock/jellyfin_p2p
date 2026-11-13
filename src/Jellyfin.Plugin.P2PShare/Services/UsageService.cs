using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Quotas;
using Jellyfin.Plugin.P2PShare.Core.Throttling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Owns the usage ledger and its persistence.
/// </summary>
public sealed class UsageService
{
    private readonly ConfigStore _config;
    private UsageLedgerStore? _store;

    public UsageService(ConfigStore config, TimeProvider time)
    {
        _config = config;
        Ledger = new UsageLedger(time);
    }

    public UsageLedger Ledger { get; }

    private UsageLedgerStore Store => _store ??= new UsageLedgerStore(Path.Combine(_config.DataFolder, "usage.json"));

    public void Load() => Store.LoadInto(Ledger);

    public void Save(bool force = false) => Store.Save(Ledger, force);
}

/// <summary>
/// Loads usage at startup, flushes it every minute and at shutdown, and keeps bandwidth
/// buckets in sync with configuration changes.
/// </summary>
public sealed class P2PShareHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);
    private readonly UsageService _usage;
    private readonly ConfigStore _config;
    private readonly BandwidthManager _bandwidth;
    private readonly ILogger<P2PShareHostedService> _logger;
    private Timer? _timer;

    public P2PShareHostedService(UsageService usage, ConfigStore config, BandwidthManager bandwidth, ILogger<P2PShareHostedService> logger)
    {
        _usage = usage;
        _config = config;
        _bandwidth = bandwidth;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _usage.Load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load P2P Share usage data; starting from zero");
        }

        _config.Changed += (_, c) => _bandwidth.ApplySettings(c.Global, c.Shares);
        _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        Flush(force: true);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void Flush(bool force = false)
    {
        try
        {
            _usage.Save(force);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save P2P Share usage data");
        }
    }
}
