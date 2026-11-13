using System.Collections.Generic;
using Jellyfin.Plugin.P2PShare.Core.Model;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.P2PShare.Configuration;

/// <summary>
/// Persisted plugin configuration. Mutate it only through <see cref="Services.ConfigStore"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets server-wide settings.</summary>
    public GlobalSettings Global { get; set; } = new();

    /// <summary>Gets or sets shares granted by this server.</summary>
    public List<OutgoingShare> Shares { get; set; } = new();

    /// <summary>Gets or sets shares received from peers.</summary>
    public List<IncomingConnection> Connections { get; set; } = new();
}
