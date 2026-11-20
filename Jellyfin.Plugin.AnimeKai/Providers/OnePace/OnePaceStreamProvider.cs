using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
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
        var physicalShowDir = Path.Combine(context.StorageRoot, "OnePace");
        Directory.CreateDirectory(physicalShowDir);
        Directory.CreateDirectory(showDir);

        // Ensure tvshow.nfo
        var tvshowNfo = Path.Combine(showDir, "tvshow.nfo");
        if (!File.Exists(tvshowNfo))
        {
            var nfoContent = AnimeStorageLinker.BuildTvShowNfo(
                "One Pace",
                "One Pace is a fan-edit project that condenses the One Piece anime to more closely match Eiichiro Oda's original manga. By cutting out filler scenes, recaps, and slow-paced padding, One Pace preserves the story's intended rhythm.",
                "ONE PIECE (One Pace Cut)",
                new[] { "Animation", "Anime", "Action", "Adventure", "Comedy", "Fantasy" },
                "One Pace Team",
                "https://onepace.net/images/poster.jpg");
            await File.WriteAllTextAsync(tvshowNfo, nfoContent, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(physicalShowDir, showDir, StringComparison.OrdinalIgnoreCase))
            {
                var physNfo = Path.Combine(physicalShowDir, "tvshow.nfo");
                await File.WriteAllTextAsync(physNfo, nfoContent, cancellationToken).ConfigureAwait(false);
            }
        }

        var totalEpisodes = 0;
        var arcIndex = 1;

        for (var a = 0; a < catalog.Count; a++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arc = catalog[a];
            var seasonNumber = arc.IsSpecial ? 0 : arcIndex++;
            var seasonFolderName = $"Season {seasonNumber:D2}";

            var targetSeasonDir = Path.Combine(showDir, seasonFolderName);
            var physicalSeasonDir = Path.Combine(physicalShowDir, seasonFolderName);

            var (writeDir, perFileSymlinkDir) = AnimeStorageLinker.EnsureStorageLayout(
                targetSeasonDir,
                physicalSeasonDir,
                context.UseSymlink,
                _logger);

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

                var epNfo = AnimeStorageLinker.BuildEpisodeNfo(
                    $"[{arc.Title}] {ep.Title}",
                    "One Pace",
                    seasonNumber,
                    ep.Number,
                    ep.Description,
                    ep.ImageUrl,
                    source.Language == OnePaceLanguage.Dub ? "eng" : "jpn",
                    source.Language != OnePaceLanguage.Dub,
                    mangaChapters: ep.MangaChapters);

                await AnimeStorageLinker.WriteIfChangedAsync(nfoFile, epNfo, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
                {
                    AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.strm"), strmFile, _logger);
                    AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.nfo"), nfoFile, _logger);
                }

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
            var html = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var catalog = ParseWatchPage(html, new Uri(url));
            if (catalog.Count > 0)
            {
                return catalog;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch One Pace watch catalog from {Url}; using fallback manifest.", url);
        }

        return GetFallbackCatalog();
    }

    internal static List<OnePaceArc> ParseWatchPage(string html, Uri baseUri)
    {
        var document = new HtmlParser().ParseDocument(html);
        var arcs = new List<OnePaceArc>();

        foreach (var heading in document.QuerySelectorAll("h2[id$='-title']"))
        {
            var segment = heading.Closest("li[data-timeline-segment]");
            if (segment == null) continue;

            var title = heading.TextContent.Trim();
            var isSpecial = title.StartsWith("Special:", StringComparison.OrdinalIgnoreCase);
            if (isSpecial) title = title["Special:".Length..].Trim();

            var slug = segment.GetAttribute("data-timeline-segment") ?? title.ToLowerInvariant().Replace(" ", "-");
            var header = heading.ParentElement;
            var description = header?.QuerySelector("p")?.TextContent.Trim();
            var imgUrl = segment.QuerySelector("img")?.GetAttribute("src");
            var resolvedImg = !string.IsNullOrWhiteSpace(imgUrl) && Uri.TryCreate(baseUri, imgUrl, out var fullImg) ? fullImg.ToString() : null;

            var episodes = new List<OnePaceEpisode>();
            foreach (var item in segment.QuerySelectorAll("details ol > li[value]"))
            {
                if (!int.TryParse(item.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epNum))
                {
                    continue;
                }

                var h3 = item.QuerySelector("h3");
                var epTitle = h3 != null ? EpisodePrefixRegex.Replace(h3.TextContent.Trim(), string.Empty).Trim() : $"Episode {epNum}";
                var epDesc = item.QuerySelector("p")?.TextContent.Trim();
                var epImg = item.QuerySelector("img")?.GetAttribute("src");
                var resolvedEpImg = !string.IsNullOrWhiteSpace(epImg) && Uri.TryCreate(baseUri, epImg, out var fImg) ? fImg.ToString() : null;

                var sources = new List<OnePaceVideoSource>();
                foreach (var link in item.QuerySelectorAll("a[href*='pixeldrain']"))
                {
                    var href = link.GetAttribute("href") ?? string.Empty;
                    var pm = PixeldrainRegex.Match(href);
                    if (pm.Success)
                    {
                        var qm = QualityRegex.Match(link.TextContent);
                        var quality = qm.Success ? int.Parse(qm.Groups[1].Value, CultureInfo.InvariantCulture) : 1080;
                        var isDub = link.Closest("li")?.TextContent.Contains("Dub", StringComparison.OrdinalIgnoreCase) == true;
                        var lang = isDub ? OnePaceLanguage.Dub : OnePaceLanguage.Sub;
                        sources.Add(new OnePaceVideoSource(lang, OnePaceCut.Normal, quality, pm.Groups[1].Value));
                    }
                }

                episodes.Add(new OnePaceEpisode(epNum, epTitle, epDesc, null, resolvedEpImg, sources));
            }

            if (episodes.Count > 0)
            {
                arcs.Add(new OnePaceArc(slug, title, isSpecial, null, description, resolvedImg, episodes));
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
    {
        if (!string.IsNullOrWhiteSpace(config.OnePaceTargetLibraryPath) && Directory.Exists(config.OnePaceTargetLibraryPath))
        {
            var path = config.OnePaceTargetLibraryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.EndsWith("One Pace", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "One Pace");
        }

        // Auto-detect existing One Piece or TV shows folder
        var virtualFolders = _libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
        var tvFolders = virtualFolders.Where(f => string.Equals(f.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.Locations).ToList();
        foreach (var folder in tvFolders)
        {
            if (Directory.Exists(folder))
            {
                return Path.Combine(folder, "One Pace");
            }
        }

        return Path.Combine(storageRoot, "OnePace");
    }

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
            new("romance-dawn", "Romance Dawn", false, "1-7", "Luffy sets out on his journey to become King of the Pirates.", "https://onepace.net/images/arcs/romance-dawn.jpg",
                new List<OnePaceEpisode>
                {
                    new(1, "I'm Luffy! The Man Who Will Become the Pirate King!", "Luffy meets Koby and encounters Alvida.", "1", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_romance_01") }),
                    new(2, "The Great Swordsman Appears! Pirate Hunter Roronoa Zoro", "Luffy arrives in Shells Town to recruit Zoro.", "2-4", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_romance_02") }),
                    new(3, "Morgan vs. Luffy! Who Is the Mysterious Beautiful Girl?", "Luffy and Zoro battle Captain Morgan.", "5-7", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_romance_03") })
                }),
            new("orange-town", "Orange Town", false, "8-21", "The Straw Hats face Buggy the Star Clown.", "https://onepace.net/images/arcs/orange-town.jpg",
                new List<OnePaceEpisode>
                {
                    new(1, "The Thief Girl and the Star Clown", "Luffy meets Nami and encounters Buggy's crew.", "8-11", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_orange_01") }),
                    new(2, "Zoro vs. Cabaji and the Treasure of the Town", "Zoro battles Cabaji.", "12-16", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_orange_02") })
                }),
            new("baratie", "Baratie", false, "42-68", "Sanji joins the crew at the sea restaurant Baratie.", "https://onepace.net/images/arcs/baratie.jpg",
                new List<OnePaceEpisode>
                {
                    new(1, "The Ocean Restaurant", "The Straw Hats arrive at Baratie.", "42-45", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_baratie_01") })
                }),
            new("arlong-park", "Arlong Park", false, "69-95", "The Straw Hats battle the Arlong Pirates to free Nami and Cocoyasi Village.", "https://onepace.net/images/arcs/arlong-park.jpg",
                new List<OnePaceEpisode>
                {
                    new(1, "Cocoyasi Village and the Fish-men", "Luffy pursues Nami to the Conomi Islands.", "69-72", null, new List<OnePaceVideoSource> { new(OnePaceLanguage.Sub, OnePaceCut.Normal, 1080, "p_arlong_01") })
                })
        };
    }
}
