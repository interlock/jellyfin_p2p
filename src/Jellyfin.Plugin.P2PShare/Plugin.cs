using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.P2PShare.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.P2PShare;

/// <summary>
/// The P2P Share plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id (must match build.yaml).</summary>
    public static readonly Guid PluginId = Guid.Parse("68f74e62-ee9d-4d89-b9e4-7e41baf0235d");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "P2P Share";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Share libraries between Jellyfin servers with invite tokens.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "P2PShare",
                DisplayName = "P2P Share",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", ns),
                EnableInMainMenu = true,
                MenuIcon = "share",
            },
            new PluginPageInfo
            {
                Name = "P2PShareJs",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.js", ns),
            },
        };
    }
}
