using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Configuration;
using Jellyfin.Plugin.Rezero.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.Services;

/// <summary>
/// Universal implementation of the Re:Zero Perfect Run synchronizer supporting both Stream (.strm) and Download (1080p MP4) modes.
/// </summary>
public class RezeroSyncService : IRezeroSyncService
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly IRezeroApiClient _apiClient;
    private readonly IRezeroDownloadService _downloadService;
    private readonly ILibraryManager? _libraryManager;
    private readonly IApplicationPaths? _applicationPaths;
    private readonly ILogger<RezeroSyncService> _logger;
    private readonly StoragePathResolver _storagePathResolver;
    private readonly PluginConfiguration? _configOverride;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroSyncService"/> class.
    /// </summary>
    public RezeroSyncService(
        IRezeroApiClient apiClient,
        IRezeroDownloadService downloadService,
        ILogger<RezeroSyncService> logger,
        ILibraryManager? libraryManager = null,
        IApplicationPaths? applicationPaths = null)
        : this(apiClient, downloadService, logger, libraryManager, applicationPaths, null, null)
    {
    }

    internal RezeroSyncService(
        IRezeroApiClient apiClient,
        IRezeroDownloadService downloadService,
        ILogger<RezeroSyncService> logger,
        ILibraryManager? libraryManager,
        IApplicationPaths? applicationPaths,
        StoragePathResolver? storagePathResolver,
        PluginConfiguration? configOverride)
    {
        _apiClient = apiClient;
        _downloadService = downloadService;
        _logger = logger;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _storagePathResolver = storagePathResolver ?? new StoragePathResolver();
        _configOverride = configOverride;
    }

    /// <inheritdoc />
    public string? ResolveSeriesPath() => ResolveSeriesPathInternal(_configOverride ?? Plugin.Instance?.Configuration, allowCreateExplicitTarget: false);

    /// <inheritdoc />
    public StorageResolutionResult? ResolveStoragePaths()
    {
        var config = _configOverride ?? Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        var seriesPath = ResolveSeriesPathInternal(config, allowCreateExplicitTarget: false);
        if (string.IsNullOrWhiteSpace(seriesPath))
        {
            return null;
        }

        var seasonFolder = string.IsNullOrWhiteSpace(config.SeasonFolder) ? "Season 00" : config.SeasonFolder;
        return _storagePathResolver.Resolve(config, seriesPath, seasonFolder, _applicationPaths?.DataPath);
    }

    /// <inheritdoc />
    public Task<int> SyncEpisodesAsync(CancellationToken cancellationToken = default, IProgress<double>? progress = null) =>
        SyncEpisodesAsync(null, cancellationToken, progress);

    /// <inheritdoc />
    public async Task<int> SyncEpisodesAsync(
        PluginConfiguration? runtimeConfigOverride,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        var isEphemeralRun = runtimeConfigOverride != null || _configOverride != null;
        var config = runtimeConfigOverride ?? _configOverride ?? Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Re:Zero plugin configuration is not loaded");
            return 0;
        }

        var seriesPath = ResolveSeriesPathInternal(config, allowCreateExplicitTarget: isEphemeralRun);
        if (string.IsNullOrWhiteSpace(seriesPath))
        {
            var msg = "Target series path could not be resolved. Configure TargetSeriesPath or StandaloneLibraryPath in settings.";
            _logger.LogWarning(msg);
            config.LastSyncStatus = msg;
            SaveConfig(isEphemeralRun);
            return 0;
        }

        if (!Directory.Exists(seriesPath))
        {
            Directory.CreateDirectory(seriesPath);
            _logger.LogInformation("Created target series directory: {Path}", seriesPath);
        }

        var isStandalone = string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase);
        if (isStandalone)
        {
            var tvshowNfoPath = Path.Combine(seriesPath, "tvshow.nfo");
            if (!File.Exists(tvshowNfoPath))
            {
                var title = string.IsNullOrWhiteSpace(config.StandaloneSeriesTitle) ? "Re:Zero - The Perfect Run" : config.StandaloneSeriesTitle;
                await File.WriteAllTextAsync(tvshowNfoPath, RezeroStorageLinker.BuildTvShowNfo(title), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
        }

        var seasonFolder = string.IsNullOrWhiteSpace(config.SeasonFolder) ? "Season 00" : config.SeasonFolder;
        var resolution = _storagePathResolver.Resolve(config, seriesPath, seasonFolder, _applicationPaths?.DataPath);
        var isDownloadMode = string.Equals(config.SyncMode, "Download", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Resolved {SyncMode} layout ({Mode}): physical={Physical}, target={Target}, symlink={UseSymlink}. {Reason}",
            isDownloadMode ? "Download" : "Stream",
            resolution.DetectionMode,
            resolution.PhysicalStoragePath,
            resolution.TargetSeasonPath,
            resolution.UseSymlink,
            resolution.Reason);

        var (writeDir, perFileSymlinkDir) = RezeroStorageLinker.EnsureStorageLayout(resolution, _logger);
        progress?.Report(5);

        var arcsData = await _apiClient.GetArcsAsync(cancellationToken).ConfigureAwait(false);
        if (arcsData?.Arcs == null || arcsData.Arcs.Count == 0)
        {
            var msg = "No arcs retrieved from videos.json API";
            _logger.LogWarning(msg);
            config.LastSyncStatus = msg;
            SaveConfig(isEphemeralRun);
            return 0;
        }

        var updatesData = await _apiClient.GetUpdatesAsync(cancellationToken).ConfigureAwait(false);
        if (updatesData?.Updates?.FirstOrDefault() is { } latestUpdate)
        {
            config.LastUpdateGuid = $"{latestUpdate.Date}_{latestUpdate.Title}";
        }

        var showTitle = isStandalone ? (config.StandaloneSeriesTitle ?? "Re:Zero - The Perfect Run") : "Re: ZERO, Starting Life in Another World";
        var syncedCount = isDownloadMode
            ? await SyncDownloadModeAsync(arcsData.Arcs, config, showTitle, writeDir, perFileSymlinkDir, progress, cancellationToken).ConfigureAwait(false)
            : await SyncStreamModeAsync(arcsData.Arcs, config, showTitle, writeDir, perFileSymlinkDir, progress, cancellationToken).ConfigureAwait(false);

        config.LastSyncUtc = DateTime.UtcNow;
        config.TotalSyncedEpisodes = syncedCount;
        config.LastSyncStatus = $"Successfully synchronized {syncedCount} {(isDownloadMode ? "downloaded" : "streaming")} episodes across {arcsData.Arcs.Count} arcs ({resolution.DetectionMode}).";
        SaveConfig(isEphemeralRun);

        _logger.LogInformation("Re:Zero sync completed ({Mode}). Synchronized {Count} episodes in {Path}", config.SyncMode, syncedCount, writeDir);

        if (!isEphemeralRun && syncedCount > 0 && _libraryManager != null)
        {
            _logger.LogInformation("Triggering Jellyfin library scan...");
            _libraryManager.QueueLibraryScan();
        }

        progress?.Report(100);
        return syncedCount;
    }

    private async Task<int> SyncStreamModeAsync(
        IReadOnlyList<ArcDto> arcs,
        PluginConfiguration config,
        string showTitle,
        string writeDir,
        string? perFileSymlinkDir,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var syncedCount = 0;
        var globalEpisodeIndex = 1;
        var qualitiesToGenerate = config.EnableMultiVersionFiles
            ? new[] { "1080p", "720p", "480p" }
            : new[] { config.PreferredQuality };

        for (var i = 0; i < arcs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arc = arcs[i];
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
                            var streamUrl = _apiClient.ResolveStreamUrl(episode, arc, lang, cap, quality, config.StreamFormat);
                            if (string.IsNullOrWhiteSpace(streamUrl))
                            {
                                continue;
                            }

                            var qualitySuffix = config.EnableMultiVersionFiles ? $" - {quality}" : string.Empty;
                            var variantSuffix = BuildVariantSuffix(languages.Count > 1, captionsList.Count > 1, lang, cap);
                            var epTitle = string.IsNullOrWhiteSpace(episode.Title) ? $"Episode {episode.Number}" : episode.Title;
                            var fileNameBase = $"Re ZERO Starting Life in Another World - S{config.SeasonNumber:D2}E{globalEpisodeIndex:D2} - [Arc {arc.Number:D2}] {SanitizeFileName(epTitle)}{variantSuffix}{qualitySuffix}";

                            var strmFile = Path.Combine(writeDir, $"{fileNameBase}.strm");
                            var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");

                            await RezeroStorageLinker.WriteIfChangedAsync(strmFile, streamUrl.Trim(), cancellationToken).ConfigureAwait(false);
                            var nfoContent = RezeroStorageLinker.BuildEpisodeNfo(arc, episode, globalEpisodeIndex, config.SeasonNumber, showTitle, variantSuffix);
                            await RezeroStorageLinker.WriteIfChangedAsync(nfoFile, nfoContent, cancellationToken).ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
                            {
                                RezeroStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.strm"), strmFile, _logger);
                                RezeroStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.nfo"), nfoFile, _logger);
                            }

                            syncedCount++;
                        }
                    }
                }

                globalEpisodeIndex++;
            }

            progress?.Report(5.0 + (((double)(i + 1) / arcs.Count) * 90.0));
        }

        return syncedCount;
    }

    private async Task<int> SyncDownloadModeAsync(
        IReadOnlyList<ArcDto> arcs,
        PluginConfiguration config,
        string showTitle,
        string writeDir,
        string? perFileSymlinkDir,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var syncedCount = 0;
        var newDownloadsCount = 0;
        var globalEpisodeIndex = 1;
        var capReached = false;

        for (var i = 0; i < arcs.Count && !capReached; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arc = arcs[i];
            var arcEpisodes = await _apiClient.GetArcEpisodesAsync(arc.Id, cancellationToken).ConfigureAwait(false);
            if (arcEpisodes?.Episodes == null || arcEpisodes.Episodes.Count == 0)
            {
                continue;
            }

            foreach (var episode in arcEpisodes.Episodes)
            {
                if (capReached)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var languages = GetLanguagesToSync(config.PreferredLanguage);
                var captionsList = GetCaptionsToSync(config.PreferredCaptions);

                foreach (var lang in languages)
                {
                    if (capReached)
                    {
                        break;
                    }

                    foreach (var cap in captionsList)
                    {
                        if (config.MaxDownloadsPerRun > 0 && newDownloadsCount >= config.MaxDownloadsPerRun)
                        {
                            capReached = true;
                            break;
                        }

                        var downloadUrl = _apiClient.ResolveDownloadUrl(episode, arc, lang, cap);
                        if (string.IsNullOrWhiteSpace(downloadUrl))
                        {
                            continue;
                        }

                        var variantSuffix = BuildVariantSuffix(languages.Count > 1, captionsList.Count > 1, lang, cap);
                        var epTitle = string.IsNullOrWhiteSpace(episode.Title) ? $"Episode {episode.Number}" : episode.Title;
                        var fileNameBase = $"Re ZERO Starting Life in Another World - S{config.SeasonNumber:D2}E{globalEpisodeIndex:D2} - [Arc {arc.Number:D2}] {SanitizeFileName(epTitle)}{variantSuffix}";

                        var mp4File = Path.Combine(writeDir, $"{fileNameBase}.mp4");
                        var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");

                        var dlResult = await _downloadService.DownloadEpisodeAsync(downloadUrl, mp4File, config.SkipExistingDownloads, cancellationToken).ConfigureAwait(false);
                        if (!dlResult.Succeeded)
                        {
                            _logger.LogWarning("Download skipped/failed for {File} ({Outcome}): {Error}", fileNameBase, dlResult.Outcome, dlResult.ErrorMessage);
                            continue;
                        }

                        if (!dlResult.Skipped)
                        {
                            newDownloadsCount++;
                            _logger.LogInformation("Downloaded 1080p MP4 ({Bytes} bytes): {File}", dlResult.BytesDownloaded, mp4File);
                        }
                        else
                        {
                            _logger.LogInformation("Skipped existing verified MP4: {File}", mp4File);
                        }

                        var nfoContent = RezeroStorageLinker.BuildEpisodeNfo(arc, episode, globalEpisodeIndex, config.SeasonNumber, showTitle, variantSuffix);
                        await RezeroStorageLinker.WriteIfChangedAsync(nfoFile, nfoContent, cancellationToken).ConfigureAwait(false);

                        if (config.CleanOppositeModeFiles)
                        {
                            RezeroStorageLinker.TryDeleteOppositeStrm(writeDir, perFileSymlinkDir, fileNameBase);
                        }

                        if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
                        {
                            RezeroStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.mp4"), mp4File, _logger);
                            RezeroStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.nfo"), nfoFile, _logger);
                        }

                        syncedCount++;

                        if (config.MaxDownloadsPerRun > 0 && newDownloadsCount >= config.MaxDownloadsPerRun)
                        {
                            capReached = true;
                            break;
                        }
                    }
                }

                globalEpisodeIndex++;
            }

            progress?.Report(5.0 + (((double)(i + 1) / arcs.Count) * 90.0));
        }

        return syncedCount;
    }

    private string? ResolveSeriesPathInternal(PluginConfiguration? config, bool allowCreateExplicitTarget)
    {
        if (config != null)
        {
            if (!string.IsNullOrWhiteSpace(config.TargetSeriesPath) &&
                (allowCreateExplicitTarget ||
                 string.Equals(config.MatchingStrategy, "CustomPath", StringComparison.OrdinalIgnoreCase) ||
                 Directory.Exists(config.TargetSeriesPath)))
            {
                return config.TargetSeriesPath;
            }

            if (string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(config.StandaloneLibraryPath) &&
                (allowCreateExplicitTarget || Directory.Exists(config.StandaloneLibraryPath)))
            {
                var seriesFolder = SanitizeFileName(config.StandaloneSeriesTitle ?? "Re:Zero - The Perfect Run");
                return Path.Combine(config.StandaloneLibraryPath, seriesFolder);
            }
        }

        if (_libraryManager != null)
        {
            try
            {
                foreach (var vf in _libraryManager.GetVirtualFolders())
                {
                    if (!string.Equals(vf.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase) || vf.Locations == null)
                    {
                        continue;
                    }

                    foreach (var loc in vf.Locations)
                    {
                        if (!Directory.Exists(loc))
                        {
                            continue;
                        }

                        if (config != null && string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase))
                        {
                            var seriesFolder = SanitizeFileName(config.StandaloneSeriesTitle ?? "Re:Zero - The Perfect Run");
                            return Path.Combine(loc, seriesFolder);
                        }

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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not query VirtualFolders for Re:Zero series");
            }
        }

        string[] candidates =
        [
            "/data/tv/Re - ZERO, Starting Life in Another World",
            "/data/tv/Re:Zero - Starting Life in Another World",
            "/opt/docker/data/media/merged/tv/Re - ZERO, Starting Life in Another World",
            "/opt/docker/data/media/local/tv/Re - ZERO, Starting Life in Another World"
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private void SaveConfig(bool isEphemeralRun)
    {
        if (!isEphemeralRun)
        {
            Plugin.Instance?.SaveConfiguration();
        }
    }

    private static List<string> GetLanguagesToSync(string preferredLanguage) => preferredLanguage.ToLowerInvariant() switch
    {
        "both" => new List<string> { "jp", "en" },
        "en" => new List<string> { "en" },
        _ => new List<string> { "jp" }
    };

    private static List<string> GetCaptionsToSync(string preferredCaptions) => preferredCaptions.ToLowerInvariant() switch
    {
        "both" => new List<string> { "cc", "off" },
        "off" => new List<string> { "off" },
        _ => new List<string> { "cc" }
    };

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
}
