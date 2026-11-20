using System;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Providers;
using Jellyfin.Plugin.Rezero.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
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
            var config = Plugin.Instance?.Configuration;
            var baseUrl = !string.IsNullOrWhiteSpace(config?.ApiBaseUrl) ? config.ApiBaseUrl : "https://rezeroperfect.run/";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.Rezero/1.0");
        });

        serviceCollection.AddSingleton<IRezeroSyncService, RezeroSyncService>();
        serviceCollection.AddSingleton<IMediaSourceProvider, RezeroMediaSourceProvider>();
    }
}
