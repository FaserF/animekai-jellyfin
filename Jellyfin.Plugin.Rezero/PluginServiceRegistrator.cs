using System;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Rezero;

/// <summary>
/// Registers services and dependencies for Re:Zero Perfect Run plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<IRezeroApiClient, RezeroApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://rezeroperfect.run/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.Rezero/1.0");
        });

        serviceCollection.AddSingleton<IRezeroSyncService, RezeroSyncService>();
    }
}
