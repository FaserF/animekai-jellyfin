using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Providers.Consumet;

/// <summary>
/// Model for Consumet anime info response.
/// </summary>
public class ConsumetAnimeInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public object? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("cover")] public string? Cover { get; set; }
    [JsonPropertyName("genres")] public List<string>? Genres { get; set; }
    [JsonPropertyName("totalEpisodes")] public int TotalEpisodes { get; set; }
    [JsonPropertyName("episodes")] public List<ConsumetEpisode>? Episodes { get; set; }

    public string GetPrimaryTitle()
    {
        if (Title is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? Id;
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("english", out var eng) && eng.GetString() is { Length: > 0 } sEng) return sEng;
                if (el.TryGetProperty("romaji", out var rom) && rom.GetString() is { Length: > 0 } sRom) return sRom;
                if (el.TryGetProperty("native", out var nat) && nat.GetString() is { Length: > 0 } sNat) return sNat;
            }
        }
        return Title?.ToString() ?? Id;
    }
}

/// <summary>
/// Model for Consumet episode item.
/// </summary>
public class ConsumetEpisode
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
}

/// <summary>
/// Model for Consumet episode watch stream response.
/// </summary>
public class ConsumetWatchResponse
{
    [JsonPropertyName("sources")] public List<ConsumetVideoSource>? Sources { get; set; }
    [JsonPropertyName("subtitles")] public List<ConsumetSubtitle>? Subtitles { get; set; }
}

/// <summary>
/// Model for Consumet video source.
/// </summary>
public class ConsumetVideoSource
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("isM3U8")] public bool IsM3U8 { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
}

/// <summary>
/// Model for Consumet subtitle track.
/// </summary>
public class ConsumetSubtitle
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("lang")] public string Lang { get; set; } = "English";
}

/// <summary>
/// Provider for Consumet / AniCli HLS Scraper Gateway.
/// Bridges an external self-hosted Consumet container (e.g. gogoanime, zoro, animepahe) to stream any anime series via direct HLS (.m3u8).
/// </summary>
public class ConsumetStreamProvider : IAnimeStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ConsumetStreamProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumetStreamProvider"/> class.
    /// </summary>
    public ConsumetStreamProvider(HttpClient httpClient, ILibraryManager libraryManager, ILogger<ConsumetStreamProvider> logger)
    {
        _httpClient = httpClient;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "consumet";

    /// <inheritdoc />
    public string DisplayName => "Consumet HLS Gateway";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableConsumet;

    /// <inheritdoc />
    public async Task<ProviderSyncResult> SyncEpisodesAsync(SyncContext context, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = context.Configuration;
        var shows = (config.ConsumetSubscribedShows ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (shows.Length == 0)
        {
            return new ProviderSyncResult(ProviderId, DisplayName, true, 0, "Consumet enabled, but no subscribed shows configured in settings.");
        }

        var baseUrl = EnsureTrailingSlash(config.ConsumetApiUrl);
        var provider = string.IsNullOrWhiteSpace(config.ConsumetProvider) ? "gogoanime" : config.ConsumetProvider.ToLowerInvariant();
        var targetBaseDir = ResolveTargetDirectory(config, context.StorageRoot);
        var physicalBaseDir = Path.Combine(context.StorageRoot, "Streams");
        Directory.CreateDirectory(physicalBaseDir);

        var totalSynced = 0;
        for (var i = 0; i < shows.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var showSlug = shows[i];
            try
            {
                var infoUrl = $"{baseUrl}anime/{provider}/info/{Uri.EscapeDataString(showSlug)}";
                var response = await _httpClient.GetAsync(infoUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Consumet info returned {Status} for {Slug} at {Url}", response.StatusCode, showSlug, infoUrl);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var info = JsonSerializer.Deserialize<ConsumetAnimeInfo>(json);
                if (info == null || info.Episodes == null || info.Episodes.Count == 0)
                {
                    continue;
                }

                var showTitle = info.GetPrimaryTitle();
                var safeTitle = Sanitize(showTitle);
                var targetShowDir = Path.Combine(targetBaseDir, safeTitle);
                var physicalShowDir = Path.Combine(physicalBaseDir, safeTitle);

                Directory.CreateDirectory(physicalShowDir);
                Directory.CreateDirectory(targetShowDir);

                // tvshow.nfo
                var tvshowNfo = Path.Combine(targetShowDir, "tvshow.nfo");
                if (!File.Exists(tvshowNfo))
                {
                    var content = AnimeStorageLinker.BuildTvShowNfo(
                        showTitle,
                        info.Description ?? string.Empty,
                        genres: info.Genres,
                        posterUrl: info.Image ?? info.Cover);
                    await File.WriteAllTextAsync(tvshowNfo, content, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(physicalShowDir, targetShowDir, StringComparison.OrdinalIgnoreCase))
                    {
                        var physNfo = Path.Combine(physicalShowDir, "tvshow.nfo");
                        await File.WriteAllTextAsync(physNfo, content, cancellationToken).ConfigureAwait(false);
                    }
                }

                var seasonFolder = "Season 01";
                var targetSeasonDir = Path.Combine(targetShowDir, seasonFolder);
                var physicalSeasonDir = Path.Combine(physicalShowDir, seasonFolder);

                var (writeDir, perFileSymlinkDir) = AnimeStorageLinker.EnsureStorageLayout(
                    targetSeasonDir,
                    physicalSeasonDir,
                    context.UseSymlink,
                    _logger);

                foreach (var ep in info.Episodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var epTitle = string.IsNullOrWhiteSpace(ep.Title) ? $"Episode {ep.Number}" : ep.Title;
                    var fileNameBase = $"{safeTitle} - S01E{ep.Number:D2} - {Sanitize(epTitle)}";

                    // Fetch watch URL
                    var watchUrl = $"{baseUrl}anime/{provider}/watch/{Uri.EscapeDataString(ep.Id)}";
                    var streamUrl = await ResolveWatchStreamUrlAsync(watchUrl, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(streamUrl))
                    {
                        continue;
                    }

                    var strmFile = Path.Combine(writeDir, $"{fileNameBase}.strm");
                    var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");

                    await AnimeStorageLinker.WriteIfChangedAsync(strmFile, streamUrl, cancellationToken).ConfigureAwait(false);

                    var epNfo = AnimeStorageLinker.BuildEpisodeNfo(
                        epTitle,
                        showTitle,
                        1,
                        ep.Number,
                        ep.Description ?? info.Description,
                        ep.Image ?? info.Image);

                    await AnimeStorageLinker.WriteIfChangedAsync(nfoFile, epNfo, cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
                    {
                        AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.strm"), strmFile, _logger);
                        AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.nfo"), nfoFile, _logger);
                    }

                    totalSynced++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not sync Consumet anime {Show}", showSlug);
            }

            progress?.Report((double)(i + 1) / shows.Length * 100.0);
        }

        return new ProviderSyncResult(ProviderId, DisplayName, true, totalSynced, $"Consumet synced {totalSynced} episodes across {shows.Length} subscribed series.");
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        // Consumet streams are self-contained HLS URLs written directly to the .strm files
        return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
    }

    private async Task<string?> ResolveWatchStreamUrlAsync(string watchApiUrl, CancellationToken cancellationToken)
    {
        try
        {
            var res = await _httpClient.GetAsync(watchApiUrl, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var watch = JsonSerializer.Deserialize<ConsumetWatchResponse>(json);
            if (watch?.Sources == null || watch.Sources.Count == 0) return null;

            // Prefer auto/default or 1080p
            var primary = watch.Sources.FirstOrDefault(s => string.Equals(s.Quality, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Quality, "default", StringComparison.OrdinalIgnoreCase))
                       ?? watch.Sources.FirstOrDefault(s => s.IsM3U8)
                       ?? watch.Sources[0];

            return primary.Url;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve stream URL from Consumet {Url}", watchApiUrl);
            return null;
        }
    }

    private string ResolveTargetDirectory(PluginConfiguration config, string storageRoot)
    {
        if (!string.IsNullOrWhiteSpace(config.ConsumetTargetLibraryPath) && Directory.Exists(config.ConsumetTargetLibraryPath))
        {
            return config.ConsumetTargetLibraryPath;
        }

        var virtualFolders = _libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
        var tvFolders = virtualFolders.Where(f => string.Equals(f.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.Locations).ToList();
        foreach (var folder in tvFolders)
        {
            if (Directory.Exists(folder))
            {
                return folder;
            }
        }

        return Path.Combine(storageRoot, "Streams");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string EnsureTrailingSlash(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "http://127.0.0.1:3000/";
        return url.EndsWith('/') ? url : url + "/";
    }
}
