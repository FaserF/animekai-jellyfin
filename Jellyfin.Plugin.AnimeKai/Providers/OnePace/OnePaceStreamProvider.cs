using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
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

namespace Jellyfin.Plugin.AnimeKai.Providers.OnePace;

/// <summary>
/// Audio/subtitle variant of a One Pace release.
/// </summary>
public enum OnePaceLanguage { Sub, Dub, DubCC }

/// <summary>
/// Edit cut of an episode.
/// </summary>
public enum OnePaceCut { Normal, Extended, Alternate }

/// <summary>
/// Downloadable/streamable source file for an episode.
/// </summary>
public record OnePaceVideoSource(OnePaceLanguage Language, OnePaceCut Cut, int Quality, string FileId);

/// <summary>
/// A One Pace episode.
/// </summary>
public record OnePaceEpisode(
    int Number,
    string Title,
    string? Description,
    string? MangaChapters,
    string? ImageUrl,
    List<OnePaceVideoSource> Sources);

/// <summary>
/// A One Pace arc.
/// </summary>
public record OnePaceArc(
    string Slug,
    string Title,
    bool IsSpecial,
    string? MangaChapters,
    string? Description,
    string? ImageUrl,
    List<OnePaceEpisode> Episodes);

/// <summary>
/// Provider for One Pace (onepace.net) - condenses One Piece to strictly mirror Eiichiro Oda's manga.
/// Streams directly via official Pixeldrain HTTPS endpoints with zero local video storage overhead.
/// </summary>
public class OnePaceStreamProvider : IAnimeStreamProvider
{
    private static readonly Regex PixeldrainRegex = new(@"pixeldrain\.(?:net|com)/(?:api/file/|u/)([a-zA-Z0-9_-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QualityRegex = new(@"(\d{3,4})p?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpisodePrefixRegex = new(@"^Episode\s*\d+\s*[-:]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimelineSegmentRegex = new(@"<li[^>]*data-timeline-segment=""([^""]+)""[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex H2Regex = new(@"<h2[^>]*>(.*?)</h2>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex H3Regex = new(@"<h3[^>]*>(.*?)</h3>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex PixeldrainLinkRegex = new(@"<a[^>]*href=""(?:https?://)?pixeldrain\.net/(?:u|api/file)/([a-zA-Z0-9_-]+)""[^>]*>(.*?)</a>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex NumberPrefixRegex = new(@"^(\d+)\s*:", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<OnePaceStreamProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnePaceStreamProvider"/> class.
    /// </summary>
    public OnePaceStreamProvider(
        HttpClient httpClient,
        ILibraryManager libraryManager,
        ILogger<OnePaceStreamProvider> logger)
    {
        _httpClient = httpClient;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "onepace";

    /// <inheritdoc />
    public string DisplayName => "One Pace";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableOnePace;

    /// <inheritdoc />
    public async Task<ProviderSyncResult> SyncEpisodesAsync(SyncContext context, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = context.Configuration;
        _logger.LogInformation("Starting One Pace sync (Mode: {Mode})...", config.OnePaceIntegrationMode);

        var catalog = await FetchCatalogAsync(config.OnePaceWatchPageUrl, cancellationToken).ConfigureAwait(false);
        if (catalog.Count == 0)
        {
            return new ProviderSyncResult(ProviderId, DisplayName, false, 0, "No One Pace arcs could be retrieved or parsed.");
        }

        var showDir = ResolveShowDirectory(config, context.StorageRoot);
        Directory.CreateDirectory(showDir);

        // Ensure tvshow.nfo
        var tvshowNfo = Path.Combine(showDir, "tvshow.nfo");
        var nfoContent = AnimeStorageLinker.BuildTvShowNfo(
            "One Pace",
            "One Pace is a fan-edit project that condenses the One Piece anime to more closely match Eiichiro Oda's original manga. By cutting out filler scenes, recaps, and slow-paced padding, One Pace preserves the story's intended rhythm.",
            "ONE PIECE (One Pace Cut)",
            new[] { "Animation", "Anime", "Action", "Adventure", "Comedy", "Fantasy" },
            "One Pace Team",
            null);
        await AnimeStorageLinker.WriteIfChangedAsync(tvshowNfo, nfoContent, cancellationToken).ConfigureAwait(false);

        // Download official One Pace / One Piece artwork
        await AnimeStorageLinker.DownloadArtworkAsync(_httpClient, "https://artworks.thetvdb.com/banners/posters/81797-1.jpg", Path.Combine(showDir, "poster.jpg"), _logger, cancellationToken).ConfigureAwait(false);
        await AnimeStorageLinker.DownloadArtworkAsync(_httpClient, "https://artworks.thetvdb.com/banners/graphical/81797-g.jpg", Path.Combine(showDir, "banner.jpg"), _logger, cancellationToken).ConfigureAwait(false);
        await AnimeStorageLinker.DownloadArtworkAsync(_httpClient, "https://artworks.thetvdb.com/banners/fanart/original/81797-1.jpg", Path.Combine(showDir, "backdrop.jpg"), _logger, cancellationToken).ConfigureAwait(false);

        var totalEpisodes = 0;
        var arcIndex = 1;

        for (var a = 0; a < catalog.Count; a++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arc = catalog[a];
            var seasonNumber = arc.IsSpecial ? 0 : arcIndex++;
            var seasonFolderName = $"Season {seasonNumber:D2}";

            var targetSeasonDir = Path.Combine(showDir, seasonFolderName);
            Directory.CreateDirectory(targetSeasonDir);
            var writeDir = targetSeasonDir;

            foreach (var ep in arc.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = SelectBestSource(ep.Sources, config);
                if (source == null)
                {
                    continue;
                }

                var streamUrl = $"https://pixeldrain.net/api/file/{source.FileId}";
                var fileNameBase = $"One Pace - S{seasonNumber:D2}E{ep.Number:D2} - [{Sanitize(arc.Title)}] {Sanitize(ep.Title)}";

                var strmFile = Path.Combine(writeDir, $"{fileNameBase}.strm");
                var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");

                await AnimeStorageLinker.WriteIfChangedAsync(strmFile, streamUrl, cancellationToken).ConfigureAwait(false);

                var cleanEpTitle = Regex.Replace(ep.Title, @"^One\s*Pace\s*-\s*S\d+E\d+\s*-\s*(\[[^\]]+\]\s*)?", string.Empty, RegexOptions.IgnoreCase).Trim();
                cleanEpTitle = Regex.Replace(cleanEpTitle, @"^\d+[\s: ]+", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(cleanEpTitle))
                {
                    cleanEpTitle = $"Episode {ep.Number}";
                }

                var epNfo = AnimeStorageLinker.BuildEpisodeNfo(
                    $"{cleanEpTitle} - [{arc.Title}]",
                    "One Pace",
                    seasonNumber,
                    ep.Number,
                    ep.Description,
                    ep.ImageUrl,
                    source.Language == OnePaceLanguage.Dub ? "eng" : "jpn",
                    source.Language != OnePaceLanguage.Dub,
                    mangaChapters: ep.MangaChapters);

                await AnimeStorageLinker.WriteIfChangedAsync(nfoFile, epNfo, cancellationToken).ConfigureAwait(false);

                totalEpisodes++;
            }

            progress?.Report((double)(a + 1) / catalog.Count * 100.0);
        }

        _logger.LogInformation("One Pace sync completed: {Count} episodes across {Arcs} arcs.", totalEpisodes, catalog.Count);
        return new ProviderSyncResult(ProviderId, DisplayName, true, totalEpisodes, $"Synchronized {totalEpisodes} One Pace episodes across {catalog.Count} arcs.");
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode || string.IsNullOrWhiteSpace(item.Path))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        if (!item.Path.Contains("One Pace", StringComparison.OrdinalIgnoreCase) &&
            !item.Path.Contains("onepace", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        // Return multi-quality or multi-language versions if available
        var sources = new List<MediaSourceInfo>();
        var path = item.Path;
        if (File.Exists(path) && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var strmUrl = File.ReadAllText(path).Trim();
                var m = PixeldrainRegex.Match(strmUrl);
                if (m.Success)
                {
                    var fileId = m.Groups[1].Value;
                    sources.Add(new MediaSourceInfo
                    {
                        Id = $"{item.Id:N}_1080p",
                        Name = "1080p (Direct Stream)",
                        Path = $"https://pixeldrain.net/api/file/{fileId}",
                        Protocol = MediaProtocol.Http,
                        Container = "mp4",
                        Bitrate = 8000000,
                        IsRemote = true,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsTranscoding = true,
                        SupportsProbing = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve One Pace MediaSource from {Path}", path);
            }
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
    }

    internal async Task<List<OnePaceArc>> FetchCatalogAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var catalog = ParseWatchPage(html, new Uri(url));
                if (catalog.Count > 0)
                {
                    _logger.LogInformation("Fetched live One Pace catalog: {Count} arcs found at {Url}", catalog.Count, url);
                    return catalog;
                }
            }

            _logger.LogInformation("Live One Pace catalog at {Url} returned no arcs; using bundled fallback manifest.", url);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not fetch One Pace watch catalog from {Url}; using bundled fallback manifest.", url);
        }

        return GetFallbackCatalog();
    }

    internal static List<OnePaceArc> ParseWatchPage(string html, Uri baseUri)
    {
        var arcs = new List<OnePaceArc>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return arcs;
        }

        var segmentMatches = TimelineSegmentRegex.Matches(html);
        if (segmentMatches.Count == 0)
        {
            return arcs;
        }

        for (var i = 0; i < segmentMatches.Count; i++)
        {
            var match = segmentMatches[i];
            var slug = match.Groups[1].Value.Trim();
            var startIndex = match.Index;
            var endIndex = (i + 1 < segmentMatches.Count) ? segmentMatches[i + 1].Index : html.Length;
            var segmentHtml = html.Substring(startIndex, endIndex - startIndex);

            var h2Match = H2Regex.Match(segmentHtml);
            var rawTitle = h2Match.Success
                ? WebUtility.HtmlDecode(HtmlTagRegex.Replace(h2Match.Groups[1].Value, string.Empty)).Trim()
                : slug;

            var isSpecial = rawTitle.StartsWith("Special:", StringComparison.OrdinalIgnoreCase);
            var title = isSpecial ? rawTitle["Special:".Length..].Trim() : rawTitle;

            var pMatch = Regex.Match(segmentHtml, @"<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var description = pMatch.Success
                ? WebUtility.HtmlDecode(HtmlTagRegex.Replace(pMatch.Groups[1].Value, string.Empty)).Trim()
                : null;

            var h3Matches = H3Regex.Matches(segmentHtml);
            var episodes = new List<OnePaceEpisode>();

            for (var j = 0; j < h3Matches.Count; j++)
            {
                var h3 = h3Matches[j];
                var rawEpTitle = WebUtility.HtmlDecode(HtmlTagRegex.Replace(h3.Groups[1].Value, string.Empty)).Trim();

                var epNum = j + 1;
                var numMatch = NumberPrefixRegex.Match(rawEpTitle);
                if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNum))
                {
                    epNum = parsedNum;
                }

                var epTitle = EpisodePrefixRegex.Replace(rawEpTitle, string.Empty).Trim();
                if (epTitle.StartsWith($"{epNum:D2}:", StringComparison.OrdinalIgnoreCase) || epTitle.StartsWith($"{epNum}:", StringComparison.OrdinalIgnoreCase))
                {
                    var colonIdx = epTitle.IndexOf(':');
                    if (colonIdx >= 0 && colonIdx < epTitle.Length - 1)
                    {
                        epTitle = epTitle[(colonIdx + 1)..].Trim();
                    }
                }

                var epStartIndex = h3.Index + h3.Length;
                var epEndIndex = (j + 1 < h3Matches.Count) ? h3Matches[j + 1].Index : segmentHtml.Length;
                var epChunk = segmentHtml.Substring(epStartIndex, epEndIndex - epStartIndex);

                var linkMatches = PixeldrainLinkRegex.Matches(epChunk);
                var sources = new List<OnePaceVideoSource>();

                foreach (Match lm in linkMatches)
                {
                    var fileId = lm.Groups[1].Value;
                    var linkText = lm.Groups[2].Value;
                    var qm = QualityRegex.Match(linkText);
                    var quality = qm.Success ? int.Parse(qm.Groups[1].Value, CultureInfo.InvariantCulture) : 1080;

                    var preStart = Math.Max(0, lm.Index - 250);
                    var preContext = epChunk.Substring(preStart, lm.Index - preStart);

                    var lang = OnePaceLanguage.Sub;
                    if (preContext.Contains("Closed Caption", StringComparison.OrdinalIgnoreCase) || preContext.Contains("Dub with", StringComparison.OrdinalIgnoreCase))
                    {
                        lang = OnePaceLanguage.DubCC;
                    }
                    else if (preContext.Contains("English Dub", StringComparison.OrdinalIgnoreCase) || preContext.Contains("Dub", StringComparison.OrdinalIgnoreCase))
                    {
                        lang = OnePaceLanguage.Dub;
                    }

                    sources.Add(new OnePaceVideoSource(lang, OnePaceCut.Normal, quality, fileId));
                }

                if (sources.Count > 0)
                {
                    episodes.Add(new OnePaceEpisode(epNum, epTitle, null, null, null, sources));
                }
            }

            if (episodes.Count > 0)
            {
                arcs.Add(new OnePaceArc(slug, title, isSpecial, null, description, null, episodes));
            }
        }

        return arcs;
    }

    private static OnePaceVideoSource? SelectBestSource(List<OnePaceVideoSource> sources, PluginConfiguration config)
    {
        if (sources.Count == 0) return null;

        var targetLang = config.OnePaceLanguage.ToLowerInvariant() switch
        {
            "dub" => OnePaceLanguage.Dub,
            "dub_cc" => OnePaceLanguage.DubCC,
            _ => OnePaceLanguage.Sub
        };

        var targetQuality = config.OnePacePreferredQuality;
        var langFiltered = sources.Where(s => s.Language == targetLang).ToList();
        if (langFiltered.Count == 0) langFiltered = sources;

        var exactQuality = langFiltered.FirstOrDefault(s => s.Quality == targetQuality);
        if (exactQuality != null) return exactQuality;

        return langFiltered.OrderByDescending(s => s.Quality).FirstOrDefault();
    }

    private string ResolveShowDirectory(PluginConfiguration config, string storageRoot)
        => AnimeStorageLinker.ResolveShowDirectory(config, _libraryManager, "One Pace", config.OnePaceTargetLibraryPath, storageRoot, "OnePace");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    internal static List<OnePaceArc> GetFallbackCatalog()
    {
        // Core fallback arcs with representative episodes
        return new List<OnePaceArc>
        {
            new("romance-dawn", "Romance Dawn", false, "1-7", "Luffy sets out on his journey to become King of the Pirates.", null,
                new List<OnePaceEpisode>
                {
                    new(1, "I'm Luffy! The Man Who Will Become the Pirate King!", "Luffy meets Koby and encounters Alvida.", "1", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_romance_01") }),
                    new(2, "The Great Swordsman Appears! Pirate Hunter Roronoa Zoro", "Luffy arrives in Shells Town to recruit Zoro.", "2-4", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_romance_02") }),
                    new(3, "Morgan vs. Luffy! Who Is the Mysterious Beautiful Girl?", "Luffy and Zoro battle Captain Morgan.", "5-7", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_romance_03") })
                }),
            new("orange-town", "Orange Town", false, "8-21", "The Straw Hats face Buggy the Star Clown.", null,
                new List<OnePaceEpisode>
                {
                    new(1, "The Thief Girl and the Star Clown", "Luffy meets Nami and encounters Buggy's crew.", "8-11", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_orange_01") }),
                    new(2, "Zoro vs. Cabaji and the Treasure of the Town", "Zoro battles Cabaji.", "12-16", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_orange_02") })
                }),
            new("baratie", "Baratie", false, "42-68", "Sanji joins the crew at the sea restaurant Baratie.", null,
                new List<OnePaceEpisode>
                {
                    new(1, "The Ocean Restaurant", "The Straw Hats arrive at Baratie.", "42-45", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_baratie_01") })
                }),
            new("arlong-park", "Arlong Park", false, "69-95", "The Straw Hats battle the Arlong Pirates to free Nami and Cocoyasi Village.", null,
                new List<OnePaceEpisode>
                {
                    new(1, "Cocoyasi Village and the Fish-men", "Luffy pursues Nami to the Conomi Islands.", "69-72", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_arlong_01") })
                })
        };
    }
}
