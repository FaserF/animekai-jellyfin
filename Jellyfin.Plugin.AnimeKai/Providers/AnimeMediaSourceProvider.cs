using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Providers;

/// <summary>
/// Unified MediaSource provider routing dynamic stream versions across all anime providers.
/// Exposes adaptive HLS streams, clean vs burned CC versions, and multi-language dubs in Jellyfin client players.
/// </summary>
public class AnimeMediaSourceProvider : IMediaSourceProvider, IHasOrder
{
    private readonly IEnumerable<IAnimeStreamProvider> _providers;
    private readonly ILogger<AnimeMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeMediaSourceProvider"/> class.
    /// </summary>
    public AnimeMediaSourceProvider(IEnumerable<IAnimeStreamProvider> providers, ILogger<AnimeMediaSourceProvider> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => -50;

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode || string.IsNullOrWhiteSpace(item.Path))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.OfferAllQualitiesInPlayer)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var results = new List<MediaSourceInfo>();
        foreach (var provider in _providers)
        {
            if (!provider.IsEnabled(config))
            {
                continue;
            }

            try
            {
                var sources = await provider.GetMediaSourcesAsync(item, cancellationToken).ConfigureAwait(false);
                if (sources != null)
                {
                    results.AddRange(sources);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking media sources for provider {Provider}", provider.ProviderId);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveStream>(null!);
}
