using System;
using System.Threading;
using Jellyfin.Plugin.AnimeKai.Api;
using Jellyfin.Plugin.AnimeKai.Providers;
using Jellyfin.Plugin.AnimeKai.Providers.BleachKai;
using Jellyfin.Plugin.AnimeKai.Providers.Consumet;
using Jellyfin.Plugin.AnimeKai.Providers.NarutoKai;
using Jellyfin.Plugin.AnimeKai.Providers.OnePace;
using Jellyfin.Plugin.AnimeKai.Providers.Rezero;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AnimeKai;

/// <summary>
/// Registers services and multi-provider dependencies for the Anime Kai &amp; Fan-Edits plugin.
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
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.AnimeKai/0.2");
        });

        serviceCollection.AddHttpClient<IRezeroDownloadService, RezeroDownloadService>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.AnimeKai/0.2");
        });

        serviceCollection.AddHttpClient<OnePaceStreamProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.AnimeKai/0.2 (OnePaceSync)");
        });

        serviceCollection.AddHttpClient<ConsumetStreamProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.AnimeKai/0.2 (ConsumetGateway)");
        });

        serviceCollection.AddHttpClient<NarutoKaiStreamProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.AnimeKai/0.2 (NarutoKaiSync)");
        });

        serviceCollection.AddHttpClient<BleachKaiStreamProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin.Plugin.AnimeKai/0.2 (BleachKaiSync)");
        });

        // Register Providers
        serviceCollection.AddSingleton<IAnimeStreamProvider, RezeroStreamProvider>();
        serviceCollection.AddSingleton<IAnimeStreamProvider, OnePaceStreamProvider>();
        serviceCollection.AddSingleton<IAnimeStreamProvider, NarutoKaiStreamProvider>();
        serviceCollection.AddSingleton<IAnimeStreamProvider, BleachKaiStreamProvider>();
        serviceCollection.AddSingleton<IAnimeStreamProvider, ConsumetStreamProvider>();

        // Register Services
        serviceCollection.AddSingleton<IAnimeSyncService, AnimeSyncService>();
        serviceCollection.AddSingleton<IMediaSourceProvider, AnimeMediaSourceProvider>();
    }
}
