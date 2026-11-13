using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.P2PShare.Configuration;
using Jellyfin.Plugin.P2PShare.Core.Model;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Serializes configuration changes and publishes immutable-by-convention snapshots for
/// hot paths (peer requests), so readers never observe a list being mutated.
/// </summary>
public sealed class ConfigStore
{
    private readonly object _lock = new();
    private PluginConfiguration? _snapshot;

    public ConfigStore()
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnExternalChange;
        }
    }

    /// <summary>Raised after every change, with the new snapshot.</summary>
    public event EventHandler<PluginConfiguration>? Changed;

    private static Plugin PluginInstance => Plugin.Instance ?? throw new InvalidOperationException("P2P Share plugin is not loaded.");

    /// <summary>Gets a snapshot of the configuration. Do not mutate it.</summary>
    public PluginConfiguration Snapshot
    {
        get
        {
            var s = _snapshot;
            if (s is not null)
            {
                return s;
            }

            lock (_lock)
            {
                return _snapshot ??= Clone(PluginInstance.Configuration);
            }
        }
    }

    /// <summary>Gets the plugin's data folder.</summary>
    public string DataFolder => PluginInstance.DataFolderPath;

    /// <summary>Gets the effective mirror root folder.</summary>
    public string MirrorRoot
    {
        get
        {
            var configured = Snapshot.Global.MirrorRootPath;
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(DataFolder, "mirrors") : configured;
        }
    }

    /// <summary>Applies a change, saves it, and publishes a new snapshot.</summary>
    public T Update<T>(Func<PluginConfiguration, T> mutate)
    {
        PluginConfiguration snapshot;
        T result;
        lock (_lock)
        {
            var config = PluginInstance.Configuration;
            result = mutate(config);
            PluginInstance.SaveConfiguration();
            snapshot = _snapshot = Clone(config);
        }

        Changed?.Invoke(this, snapshot);
        return result;
    }

    public void Update(Action<PluginConfiguration> mutate) => Update<object?>(c =>
    {
        mutate(c);
        return null;
    });

    public OutgoingShare? FindShare(Guid id) => Snapshot.Shares.FirstOrDefault(s => s.Id == id);

    public IncomingConnection? FindConnection(Guid id) => Snapshot.Connections.FirstOrDefault(c => c.Id == id);

    private void OnExternalChange(object? sender, BasePluginConfiguration e)
    {
        PluginConfiguration snapshot;
        lock (_lock)
        {
            snapshot = _snapshot = Clone(PluginInstance.Configuration);
        }

        Changed?.Invoke(this, snapshot);
    }

    private static PluginConfiguration Clone(PluginConfiguration config)
        => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.SerializeToUtf8Bytes(config)) ?? new PluginConfiguration();
}
