using System;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Jellyfin.Plugin.P2PShare.Core.Throttling;
using Jellyfin.Plugin.P2PShare.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.P2PShare;

/// <summary>
/// Registers the plugin's services with Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ConfigStore>();
        serviceCollection.AddSingleton<UsageService>();
        serviceCollection.AddSingleton(sp => new BandwidthManager(sp.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton(sp => new ActiveStreamRegistry(sp.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton(_ => new ThrottledCopier());
        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddSingleton<PeerStreamService>();
        serviceCollection.AddSingleton<LibraryRegistrar>();
        serviceCollection.AddSingleton<RemoteSyncService>();
        serviceCollection.AddSingleton<PeerClientFactory>();
        serviceCollection.AddHostedService<P2PShareHostedService>();
    }
}
