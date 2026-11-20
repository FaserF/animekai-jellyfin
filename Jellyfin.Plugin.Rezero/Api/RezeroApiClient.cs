using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.Api;

/// <summary>
/// Client for fetching release data and stream references from rezeroperfect.run.
/// </summary>
public class RezeroApiClient : IRezeroApiClient
{
    private const string DefaultSiteBaseUrl = "https://rezeroperfect.run/";
    private const string DefaultHlsBaseUrl = "https://media.rezeroperfect.run/HLS/";
    private const string DefaultDownloadsBaseUrl = "https://media.rezeroperfect.run/downloads/";
    private const string DefaultTorrentsBaseUrl = "https://media.rezeroperfect.run/torrents/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<RezeroApiClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroApiClient"/> class.
    /// </summary>
    public RezeroApiClient(HttpClient httpClient, ILogger<RezeroApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        if (_httpClient.BaseAddress == null)
        {
            var baseUrl = EnsureTrailingSlash(Plugin.Instance?.Configuration?.ApiBaseUrl, DefaultSiteBaseUrl);
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    /// <inheritdoc />
    public async Task<VideosResponse?> GetArcsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("juicy_prototype/videos.json", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VideosResponse>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve arcs from videos.json");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<EpisodeDataResponse?> GetArcEpisodesAsync(string arcId, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = $"episodes/{arcId}.json";
            var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EpisodeDataResponse>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve episode data for arc '{ArcId}'", arcId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UpdatesResponse?> GetUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("juicy_prototype/updates.json", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdatesResponse>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve updates from updates.json");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TorrentManifestDto?> GetTorrentManifestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var torrentsBase = EnsureTrailingSlash(Plugin.Instance?.Configuration?.TorrentsBaseUrl, DefaultTorrentsBaseUrl);
            var response = await _httpClient.GetAsync($"{torrentsBase}torrents.json", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TorrentManifestDto>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve torrents.json catalog");
            return null;
        }
    }

    /// <inheritdoc />
    public string? ResolveDownloadUrl(
        EpisodeDto episode,
        ArcDto arc,
        string language,
        string captions)
    {
        var relativePath = SelectRelativeSourcePath(episode, language, captions, "1080p");
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var downloadsBase = EnsureTrailingSlash(Plugin.Instance?.Configuration?.DownloadsBaseUrl, DefaultDownloadsBaseUrl);
        return BuildDirectMp4Url(relativePath, downloadsBase);
    }

    /// <inheritdoc />
    public string? ResolveStreamUrl(
        EpisodeDto episode,
        ArcDto arc,
        string language,
        string captions,
        string quality,
        string format)
    {
        var relativePath = SelectRelativeSourcePath(episode, language, captions, quality);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var hlsBase = EnsureTrailingSlash(Plugin.Instance?.Configuration?.HlsBaseUrl, DefaultHlsBaseUrl);
        var downloadsBase = EnsureTrailingSlash(Plugin.Instance?.Configuration?.DownloadsBaseUrl, DefaultDownloadsBaseUrl);

        // Direct MP4 requested
        if (string.Equals(format, "direct_mp4", StringComparison.OrdinalIgnoreCase))
        {
            if (BuildDirectMp4Url(relativePath, downloadsBase) is { } directMp4Url)
            {
                return directMp4Url;
            }
        }

        // HLS Auto requested (adaptive bitrate master playlist)
        if (string.Equals(format, "hls_auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(quality, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "hls", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(format))
        {
            var autoPath = Regex.Replace(relativePath, @"/(1080p|720p|480p)/", "/auto/", RegexOptions.IgnoreCase);
            return autoPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? autoPath
                : $"{hlsBase}{autoPath.TrimStart('/')}";
        }

        // Fallback or specific HLS resolution requested
        return relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"{hlsBase}{relativePath.TrimStart('/')}";
    }

    private static string? SelectRelativeSourcePath(
        EpisodeDto episode,
        string language,
        string captions,
        string preferredQuality)
    {
        if (episode.Sources == null ||
            !episode.Sources.TryGetValue(language, out var captionDict) ||
            !captionDict.TryGetValue(captions, out var resolutionDict))
        {
            return null;
        }

        if (!resolutionDict.TryGetValue(preferredQuality, out var relativePath) &&
            !resolutionDict.TryGetValue("1080p", out relativePath) &&
            !resolutionDict.TryGetValue("720p", out relativePath))
        {
            resolutionDict.TryGetValue("480p", out relativePath);
        }

        return string.IsNullOrWhiteSpace(relativePath) ? null : relativePath;
    }

    private static string? BuildDirectMp4Url(string relativePath, string downloadsBase)
    {
        var parts = relativePath.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5 && parts[^1] == "index.m3u8")
        {
            var arcPart = parts[^5];
            var epPart = parts[^4];
            var namePart = parts[^2];
            return $"{downloadsBase}{arcPart}/{epPart}/{namePart}.mp4";
        }

        return null;
    }

    private static string EnsureTrailingSlash(string? url, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
        return value.EndsWith('/') ? value : value + "/";
    }
}
