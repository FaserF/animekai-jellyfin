using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.Services;

/// <summary>
/// Universal implementation of the Re:Zero Perfect Run streaming synchronizer.
/// Supports both Special Season integration and Standalone Series creation across any environment.
/// </summary>
public class RezeroSyncService : IRezeroSyncService
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly IRezeroApiClient _apiClient;
    private readonly ILibraryManager? _libraryManager;
    private readonly ILogger<RezeroSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroSyncService"/> class.
    /// </summary>
    public RezeroSyncService(
        IRezeroApiClient apiClient,
        ILogger<RezeroSyncService> logger,
        ILibraryManager? libraryManager = null)
    {
        _apiClient = apiClient;
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public async Task<int> SyncEpisodesAsync(CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Re:Zero plugin configuration is not loaded");
            return 0;
        }

        var seriesPath = ResolveSeriesPath();
        if (string.IsNullOrWhiteSpace(seriesPath))
        {
            var msg = "Target series path could not be resolved. Configure TargetSeriesPath or StandaloneLibraryPath in settings.";
            _logger.LogWarning(msg);
            config.LastSyncStatus = msg;
            Plugin.Instance?.SaveConfiguration();
            return 0;
        }

        if (!Directory.Exists(seriesPath))
        {
            Directory.CreateDirectory(seriesPath);
            _logger.LogInformation("Created target series directory: {Path}", seriesPath);
        }

        // If Standalone Series mode, create tvshow.nfo if not present
        var isStandalone = string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase);
        if (isStandalone)
        {
            var tvshowNfoPath = Path.Combine(seriesPath, "tvshow.nfo");
            if (!File.Exists(tvshowNfoPath))
            {
                var title = string.IsNullOrWhiteSpace(config.StandaloneSeriesTitle) ? "Re:Zero - The Perfect Run" : config.StandaloneSeriesTitle;
                var tvshowNfo = BuildTvShowNfo(title);
                await File.WriteAllTextAsync(tvshowNfoPath, tvshowNfo, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
        }

        var seasonFolder = string.IsNullOrWhiteSpace(config.SeasonFolder) ? "Season 00" : config.SeasonFolder;
        var targetSeasonDir = Path.Combine(seriesPath, seasonFolder);
        if (!Directory.Exists(targetSeasonDir))
        {
            Directory.CreateDirectory(targetSeasonDir);
            _logger.LogInformation("Created season directory: {Path}", targetSeasonDir);
        }

        progress?.Report(5);

        // 1. Fetch metadata & check updates
        var arcsData = await _apiClient.GetArcsAsync(cancellationToken).ConfigureAwait(false);
        if (arcsData?.Arcs == null || arcsData.Arcs.Count == 0)
        {
            var msg = "No arcs retrieved from videos.json API";
            _logger.LogWarning(msg);
            config.LastSyncStatus = msg;
            Plugin.Instance?.SaveConfiguration();
            return 0;
        }

        var updatesData = await _apiClient.GetUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var latestUpdate = updatesData?.Updates?.FirstOrDefault();
        if (latestUpdate != null)
        {
            config.LastUpdateGuid = $"{latestUpdate.Date}_{latestUpdate.Title}";
        }

        var totalArcs = arcsData.Arcs.Count;
        var syncedEpisodesCount = 0;
        var globalEpisodeIndex = 1;

        var qualitiesToGenerate = config.EnableMultiVersionFiles
            ? new[] { "1080p", "720p", "480p" }
            : new[] { config.PreferredQuality };

        var showTitle = isStandalone ? (config.StandaloneSeriesTitle ?? "Re:Zero - The Perfect Run") : "Re: ZERO, Starting Life in Another World";

        // 2. Process Arcs and Episodes
        for (var i = 0; i < totalArcs; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var arc = arcsData.Arcs[i];
            var arcEpisodes = await _apiClient.GetArcEpisodesAsync(arc.Id, cancellationToken).ConfigureAwait(false);
            if (arcEpisodes?.Episodes == null || arcEpisodes.Episodes.Count == 0)
            {
                continue;
            }

            foreach (var episode in arcEpisodes.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var languages = GetLanguagesToSync(config.PreferredLanguage);
                var captionsList = GetCaptionsToSync(config.PreferredCaptions);

                foreach (var lang in languages)
                {
                    foreach (var cap in captionsList)
                    {
                        foreach (var quality in qualitiesToGenerate)
                        {
                            var streamUrl = _apiClient.ResolveStreamUrl(
                                episode,
                                arc,
                                lang,
                                cap,
                                quality,
                                config.StreamFormat);

                            if (string.IsNullOrWhiteSpace(streamUrl))
                            {
                                continue;
                            }

                            var qualitySuffix = config.EnableMultiVersionFiles ? $" - {quality}" : string.Empty;
                            var variantSuffix = BuildVariantSuffix(languages.Count > 1, captionsList.Count > 1, lang, cap);
                            var epTitle = string.IsNullOrWhiteSpace(episode.Title) ? $"Episode {episode.Number}" : episode.Title;
                            var fileNameBase = $"Re ZERO Starting Life in Another World - S{config.SeasonNumber:D2}E{globalEpisodeIndex:D2} - [Arc {arc.Number:D2}] {SanitizeFileName(epTitle)}{variantSuffix}{qualitySuffix}";

                            var strmFile = Path.Combine(targetSeasonDir, $"{fileNameBase}.strm");
                            var nfoFile = Path.Combine(targetSeasonDir, $"{fileNameBase}.nfo");

                            // Write .strm pointer
                            await WriteIfChangedAsync(strmFile, streamUrl.Trim(), cancellationToken).ConfigureAwait(false);

                            // Write .nfo metadata
                            var nfoContent = BuildEpisodeNfo(arc, episode, globalEpisodeIndex, config.SeasonNumber, showTitle, epTitle, variantSuffix);
                            await WriteIfChangedAsync(nfoFile, nfoContent, cancellationToken).ConfigureAwait(false);

                            syncedEpisodesCount++;
                        }
                    }
                }

                globalEpisodeIndex++;
            }

            var currentProgress = 5.0 + (((double)(i + 1) / totalArcs) * 90.0);
            progress?.Report(currentProgress);
        }

        config.LastSyncUtc = DateTime.UtcNow;
        config.TotalSyncedEpisodes = syncedEpisodesCount;
        config.LastSyncStatus = $"Successfully synchronized {syncedEpisodesCount} streaming episodes across {totalArcs} arcs.";
        Plugin.Instance?.SaveConfiguration();

        _logger.LogInformation("Re:Zero sync completed. Synchronized {Count} stream pointers to {Path}", syncedEpisodesCount, targetSeasonDir);

        if (syncedEpisodesCount > 0 && _libraryManager != null)
        {
            _logger.LogInformation("Triggering Jellyfin library scan...");
            _libraryManager.QueueLibraryScan();
        }

        progress?.Report(100);
        return syncedEpisodesCount;
    }

    /// <inheritdoc />
    public string? ResolveSeriesPath()
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null)
        {
            // Custom path specified
            if (!string.IsNullOrWhiteSpace(config.TargetSeriesPath) && Directory.Exists(config.TargetSeriesPath))
            {
                return config.TargetSeriesPath;
            }

            // Standalone series mode
            if (string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(config.StandaloneLibraryPath) && Directory.Exists(config.StandaloneLibraryPath))
                {
                    var seriesFolder = SanitizeFileName(config.StandaloneSeriesTitle ?? "Re:Zero - The Perfect Run");
                    return Path.Combine(config.StandaloneLibraryPath, seriesFolder);
                }
            }
        }

        // Try to find series in Jellyfin library virtual folders
        if (_libraryManager != null)
        {
            try
            {
                foreach (var vf in _libraryManager.GetVirtualFolders())
                {
                    if (string.Equals(vf.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase) && vf.Locations != null)
                    {
                        foreach (var loc in vf.Locations)
                        {
                            if (!Directory.Exists(loc))
                            {
                                continue;
                            }

                            // If standalone mode and library path not set, use first TV library root
                            if (config != null && string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase))
                            {
                                var seriesFolder = SanitizeFileName(config.StandaloneSeriesTitle ?? "Re:Zero - The Perfect Run");
                                return Path.Combine(loc, seriesFolder);
                            }

                            // Match existing series folder
                            var matchedDir = Directory.EnumerateDirectories(loc)
                                .FirstOrDefault(d =>
                                {
                                    var name = Path.GetFileName(d);
                                    return name.Contains("Re:Zero", StringComparison.OrdinalIgnoreCase) ||
                                           name.Contains("Re - ZERO", StringComparison.OrdinalIgnoreCase);
                                });

                            if (matchedDir != null)
                            {
                                return matchedDir;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not query VirtualFolders for Re:Zero series");
            }
        }

        // Fallback known Docker/Linux mount locations
        string[] candidates =
        [
            "/data/tv/Re - ZERO, Starting Life in Another World",
            "/data/tv/Re:Zero - Starting Life in Another World",
            "/opt/docker/data/media/merged/tv/Re - ZERO, Starting Life in Another World",
            "/opt/docker/data/media/local/tv/Re - ZERO, Starting Life in Another World"
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static List<string> GetLanguagesToSync(string preferredLanguage)
    {
        return preferredLanguage.ToLowerInvariant() switch
        {
            "both" => new List<string> { "jp", "en" },
            "en" => new List<string> { "en" },
            _ => new List<string> { "jp" }
        };
    }

    private static List<string> GetCaptionsToSync(string preferredCaptions)
    {
        return preferredCaptions.ToLowerInvariant() switch
        {
            "both" => new List<string> { "cc", "off" },
            "off" => new List<string> { "off" },
            _ => new List<string> { "cc" }
        };
    }

    private static string BuildVariantSuffix(bool multiLang, bool multiCaptions, string lang, string cap)
    {
        if (!multiLang && !multiCaptions)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (multiLang)
        {
            parts.Add(lang.ToUpperInvariant());
        }

        if (multiCaptions)
        {
            parts.Add(cap == "cc" ? "Subtitles" : "Clean");
        }

        return parts.Count > 0 ? $" [{string.Join(" ", parts)}]" : string.Empty;
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        }

        return sb.ToString().Trim();
    }

    private static string BuildEpisodeNfo(
        ArcDto arc,
        EpisodeDto episode,
        int globalEpisodeIndex,
        int seasonNumber,
        string showTitle,
        string epTitle,
        string variantSuffix)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("episodedetails",
                new XElement("title", $"[The Perfect Run] Arc {arc.Number:D2}: {arc.Title} - Episode {episode.Number}{variantSuffix}"),
                new XElement("showtitle", showTitle),
                new XElement("season", seasonNumber),
                new XElement("episode", globalEpisodeIndex),
                new XElement("plot", arc.Description),
                new XElement("thumb", $"https://rezeroperfect.run{arc.Artwork}"),
                episode.Segments?.Intro != null ? new XElement("intro",
                    new XElement("start", episode.Segments.Intro.Start),
                    new XElement("end", episode.Segments.Intro.End)) : null,
                episode.Segments?.Outro != null ? new XElement("outro",
                    new XElement("start", episode.Segments.Outro.Start),
                    new XElement("end", episode.Segments.Outro.End)) : null
            )
        );

        return doc.ToString();
    }

    private static string BuildTvShowNfo(string title)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("tvshow",
                new XElement("title", title),
                new XElement("originaltitle", "Re:ゼロから始める異世界生活 - The Perfect Run"),
                new XElement("plot", "Experience the world of Re:Zero through the lens of those around Subaru without deaths or loops. To those who never see his failures, just how much of an enigma must he seem?"),
                new XElement("genre", "Animation"),
                new XElement("genre", "Anime"),
                new XElement("genre", "Fantasy"),
                new XElement("studio", "White Fox"),
                new XElement("year", "2026")
            )
        );

        return doc.ToString();
    }

    private static async Task WriteIfChangedAsync(string filePath, string newContent, CancellationToken cancellationToken)
    {
        if (File.Exists(filePath))
        {
            var existing = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing.Trim(), newContent.Trim(), StringComparison.Ordinal))
            {
                return;
            }
        }

        await File.WriteAllTextAsync(filePath, newContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }
}
