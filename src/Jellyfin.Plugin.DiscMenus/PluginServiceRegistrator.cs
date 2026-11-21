using Jellyfin.Plugin.DiscMenus.Media;
using Jellyfin.Plugin.DiscMenus.Native;
using Jellyfin.Plugin.DiscMenus.Sessions;
using Jellyfin.Plugin.DiscMenus.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>Registers the plugin's services with the server.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<NativeRuntimeManager>();
        serviceCollection.AddSingleton<PairingService>();
        serviceCollection.AddSingleton<StreamUrls>();
        serviceCollection.AddSingleton<MenuSessionManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MenuSessionManager>());
        serviceCollection.AddHostedService<WebIntegration>();
    }
}
