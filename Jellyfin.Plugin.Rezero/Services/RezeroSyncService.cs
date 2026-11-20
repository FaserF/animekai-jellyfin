using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Configuration;
using Jellyfin.Plugin.Rezero.Models;
using MediaBrowser.Common.Configuration;
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
    private readonly IApplicationPaths? _applicationPaths;
    private readonly ILogger<RezeroSyncService> _logger;
    private readonly StoragePathResolver _storagePathResolver;
    private readonly PluginConfiguration? _configOverride;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroSyncService"/> class.
    /// </summary>
    public RezeroSyncService(
        IRezeroApiClient apiClient,
        ILogger<RezeroSyncService> logger,
        ILibraryManager? libraryManager = null,
        IApplicationPaths? applicationPaths = null)
        : this(apiClient, logger, libraryManager, applicationPaths, null, null)
    {
    }

    internal RezeroSyncService(
        IRezeroApiClient apiClient,
        ILogger<RezeroSyncService> logger,
        ILibraryManager? libraryManager,
        IApplicationPaths? applicationPaths,
        StoragePathResolver? storagePathResolver,
        PluginConfiguration? configOverride)
    {
        _apiClient = apiClient;
        _logger = logger;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _storagePathResolver = storagePathResolver ?? new StoragePathResolver();
        _configOverride = configOverride;
    }

    /// <inheritdoc />
    public StorageResolutionResult? ResolveStoragePaths()
    {
        var config = _configOverride ?? Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        var seriesPath = ResolveSeriesPath();
        if (string.IsNullOrWhiteSpace(seriesPath))
        {
            return null;
        }

        var seasonFolder = string.IsNullOrWhiteSpace(config.SeasonFolder) ? "Season 00" : config.SeasonFolder;
        return _storagePathResolver.Resolve(config, seriesPath, seasonFolder, _applicationPaths?.DataPath);
    }

    /// <inheritdoc />
    public async Task<int> SyncEpisodesAsync(CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var config = _configOverride ?? Plugin.Instance?.Configuration;
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
            SaveConfig();
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
        var resolution = _storagePathResolver.Resolve(config, seriesPath, seasonFolder, _applicationPaths?.DataPath);
        _logger.LogInformation(
            "Resolved storage layout ({Mode}): physical={Physical}, target={Target}, symlink={UseSymlink}. {Reason}",
            resolution.DetectionMode,
            resolution.PhysicalStoragePath,
            resolution.TargetSeasonPath,
            resolution.UseSymlink,
            resolution.Reason);

        var (writeDir, perFileSymlinkDir) = EnsureStorageLayout(resolution);

        progress?.Report(5);

        // 1. Fetch metadata & check updates
        var arcsData = await _apiClient.GetArcsAsync(cancellationToken).ConfigureAwait(false);
        if (arcsData?.Arcs == null || arcsData.Arcs.Count == 0)
        {
            var msg = "No arcs retrieved from videos.json API";
            _logger.LogWarning(msg);
            config.LastSyncStatus = msg;
            SaveConfig();
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

                            var strmFileName = $"{fileNameBase}.strm";
                            var nfoFileName = $"{fileNameBase}.nfo";
                            var strmFile = Path.Combine(writeDir, strmFileName);
                            var nfoFile = Path.Combine(writeDir, nfoFileName);

                            // Write .strm pointer
                            await WriteIfChangedAsync(strmFile, streamUrl.Trim(), cancellationToken).ConfigureAwait(false);

                            // Write .nfo metadata
                            var nfoContent = BuildEpisodeNfo(arc, episode, globalEpisodeIndex, config.SeasonNumber, showTitle, epTitle, variantSuffix);
                            await WriteIfChangedAsync(nfoFile, nfoContent, cancellationToken).ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
                            {
                                EnsureFileSymlink(Path.Combine(perFileSymlinkDir, strmFileName), strmFile);
                                EnsureFileSymlink(Path.Combine(perFileSymlinkDir, nfoFileName), nfoFile);
                            }

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
        config.LastSyncStatus = $"Successfully synchronized {syncedEpisodesCount} streaming episodes across {totalArcs} arcs ({resolution.DetectionMode}).";
        SaveConfig();

        _logger.LogInformation("Re:Zero sync completed. Synchronized {Count} stream pointers to {Path}", syncedEpisodesCount, writeDir);

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
        var config = _configOverride ?? Plugin.Instance?.Configuration;
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

    private void SaveConfig()
    {
        if (_configOverride == null)
        {
            Plugin.Instance?.SaveConfiguration();
        }
    }

    private (string WriteDir, string? PerFileSymlinkDir) EnsureStorageLayout(StorageResolutionResult resolution)
    {
        var physicalDir = resolution.PhysicalStoragePath;
        var targetSeasonDir = resolution.TargetSeasonPath;

        Directory.CreateDirectory(physicalDir);

        if (!resolution.UseSymlink || StoragePathResolver.IsSameOrNestedPath(targetSeasonDir, physicalDir))
        {
            return (physicalDir, null);
        }

        try
        {
            var targetInfo = new DirectoryInfo(targetSeasonDir);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (targetInfo.LinkTarget != null)
            {
                var targetParent = Path.GetDirectoryName(targetSeasonDir) ?? Path.GetPathRoot(targetSeasonDir) ?? string.Empty;
                var resolvedExisting = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetInfo.LinkTarget, targetParent));
                var normalizedPhysical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalDir));
                if (string.Equals(resolvedExisting, normalizedPhysical, comparison))
                {
                    return (physicalDir, null);
                }

                Directory.Delete(targetSeasonDir, recursive: false);
                Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                _logger.LogInformation("Updated directory symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                return (physicalDir, null);
            }

            if (!Directory.Exists(targetSeasonDir) && !File.Exists(targetSeasonDir))
            {
                Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                _logger.LogInformation("Created directory symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                return (physicalDir, null);
            }

            if (Directory.Exists(targetSeasonDir))
            {
                var entries = Directory.EnumerateFileSystemEntries(targetSeasonDir).ToList();
                if (entries.Count == 0)
                {
                    Directory.Delete(targetSeasonDir, recursive: false);
                    Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                    _logger.LogInformation("Replaced empty directory with symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                    return (physicalDir, null);
                }

                if (entries.All(e => !Directory.Exists(e) && IsGeneratedRezeroFile(e)))
                {
                    foreach (var entry in entries)
                    {
                        File.Delete(entry);
                    }

                    Directory.Delete(targetSeasonDir, recursive: false);
                    Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                    _logger.LogInformation("Migrated existing Re:Zero stream folder to symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                    return (physicalDir, null);
                }

                _logger.LogInformation(
                    "Existing files preserved in {Target}; linking Re:Zero files individually from {Physical}",
                    targetSeasonDir,
                    physicalDir);
                return (physicalDir, targetSeasonDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create symlink {Target} -> {Physical}; falling back to direct writes.", targetSeasonDir, physicalDir);
        }

        Directory.CreateDirectory(targetSeasonDir);
        return (targetSeasonDir, null);
    }

    private void EnsureFileSymlink(string linkPath, string targetFilePath)
    {
        try
        {
            var fileInfo = new FileInfo(linkPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (fileInfo.LinkTarget != null)
            {
                var linkDir = Path.GetDirectoryName(linkPath) ?? string.Empty;
                var resolvedExisting = Path.GetFullPath(fileInfo.LinkTarget, linkDir);
                if (string.Equals(resolvedExisting, Path.GetFullPath(targetFilePath), comparison))
                {
                    return;
                }

                File.Delete(linkPath);
            }
            else if (File.Exists(linkPath))
            {
                if (!IsGeneratedRezeroFile(linkPath))
                {
                    return;
                }

                File.Delete(linkPath);
            }

            File.CreateSymbolicLink(linkPath, targetFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create per-file symlink {Link} -> {Target}", linkPath, targetFilePath);
        }
    }

    private static bool IsGeneratedRezeroFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(filePath);
        return name.StartsWith("Re ZERO Starting Life in Another World - S", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("[Arc ", StringComparison.OrdinalIgnoreCase);
    }
}
