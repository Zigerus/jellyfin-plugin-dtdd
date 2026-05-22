using System;
using Jellyfin.Plugin.Dtdd.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Dtdd;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(DtddConstants.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        serviceCollection.AddSingleton<DtddClient>();
        serviceCollection.AddSingleton<WarningCache>();
        serviceCollection.AddSingleton<UserPreferenceStore>();
    }
}
