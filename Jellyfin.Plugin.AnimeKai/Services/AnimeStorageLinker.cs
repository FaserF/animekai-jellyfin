using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeKai.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Services;

/// <summary>
/// Universal storage, symlink, and NFO serialization helper for anime fan-edits and recuts.
/// </summary>
internal static class AnimeStorageLinker
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static bool EnsureStoragePathInLibrary(
        ILibraryManager libraryManager,
        string storageRoot,
        ILogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storageRoot))
            {
                return false;
            }

            Directory.CreateDirectory(storageRoot);

            var virtualFolders = libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
            var tvFolder = virtualFolders.FirstOrDefault(v =>
                string.Equals(v.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(v.Name, "Serien", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(v.Name, "TV Shows", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(v.Name, "Anime", StringComparison.OrdinalIgnoreCase)))
                ?? virtualFolders.FirstOrDefault(v =>
                    string.Equals(v.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase));

            if (tvFolder == null)
            {
                logger.LogWarning("No TV shows library found to auto-register anime storage path {Path}", storageRoot);
                return false;
            }

            var normalizedStorage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
            var locations = tvFolder.Locations ?? Array.Empty<string>();
            var exists = locations.Any(loc =>
                !string.IsNullOrWhiteSpace(loc) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc)), normalizedStorage, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                logger.LogDebug("Anime storage path {Path} is already registered in library {Library}", storageRoot, tvFolder.Name);
                return true;
            }

            libraryManager.AddMediaPath(tvFolder.Name, new MediaBrowser.Model.Configuration.MediaPathInfo
            {
                Path = storageRoot
            });

            logger.LogInformation("Auto-registered dedicated anime storage path {Path} into TV library '{Library}'", storageRoot, tvFolder.Name);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not auto-register storage path {Path} into Jellyfin TV library", storageRoot);
            return false;
        }
    }

    internal static void CleanupLegacyTargetSymlinks(
        ILibraryManager libraryManager,
        string storageRoot,
        ILogger logger)
    {
        try
        {
            var virtualFolders = libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
            var normalizedStorage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));

            foreach (var vf in virtualFolders.Where(v => string.Equals(v.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var loc in vf.Locations ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) continue;
                    var normalizedLoc = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc));
                    if (string.Equals(normalizedLoc, normalizedStorage, StringComparison.OrdinalIgnoreCase)) continue;

                    // Clean Re:Zero Season 00 symlink from other library locations
                    try
                    {
                        var rezeroDirs = Directory.GetDirectories(loc, "*Re*ZERO*", SearchOption.TopDirectoryOnly);
                        foreach (var rDir in rezeroDirs)
                        {
                            var s00 = Path.Combine(rDir, "Season 00");
                            if (Directory.Exists(s00))
                            {
                                var dirInfo = new DirectoryInfo(s00);
                                if (dirInfo.LinkTarget != null || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    Directory.Delete(s00, recursive: false);
                                    logger.LogInformation("Removed legacy Season 00 symlink from {Path}", s00);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not clean Re:Zero legacy symlink in {Path}", loc);
                    }

                    // Clean standalone show folders in other locations if they contain symlinks
                    var fanEditNames = new[] { "Naruto Kai", "Bleach Kai", "One Pace" };
                    foreach (var showName in fanEditNames)
                    {
                        try
                        {
                            var candidate = Path.Combine(loc, showName);
                            if (Directory.Exists(candidate))
                            {
                                var dirInfo = new DirectoryInfo(candidate);
                                if (dirInfo.LinkTarget != null || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    Directory.Delete(candidate, recursive: false);
                                    logger.LogInformation("Removed legacy show symlink {Path}", candidate);
                                }
                                else
                                {
                                    var subEntries = Directory.GetFileSystemEntries(candidate);
                                    var hasRealMedia = subEntries.Any(e => e.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
                                    if (!hasRealMedia)
                                    {
                                        Directory.Delete(candidate, recursive: true);
                                        logger.LogInformation("Removed legacy symlink show folder {Path}", candidate);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Could not clean legacy show folder {Show} in {Path}", showName, loc);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Legacy symlink cleanup completed with non-fatal notice.");
        }
    }

    internal static string? ResolveTargetTvLibraryRoot(
        PluginConfiguration config,
        ILibraryManager libraryManager,
        string? providerOverridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(providerOverridePath) && Directory.Exists(providerOverridePath))
            return providerOverridePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(config.DefaultTargetLibraryPath) && Directory.Exists(config.DefaultTargetLibraryPath))
            return config.DefaultTargetLibraryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var virtualFolders = libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
        var candidates = new List<(string Path, int Score)>();

        foreach (var vf in virtualFolders.Where(v => string.Equals(v.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase)))
        {
            var vfName = vf.Name ?? string.Empty;
            foreach (var loc in (vf.Locations ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l) && Directory.Exists(l)))
            {
                var score = string.Equals(loc, "/data/tv", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
                if (string.Equals(vfName, "Serien", StringComparison.OrdinalIgnoreCase) || string.Equals(vfName, "TV Shows", StringComparison.OrdinalIgnoreCase) || string.Equals(vfName, "Anime", StringComparison.OrdinalIgnoreCase))
                    score += 50;
                if (loc.Contains("emby", StringComparison.OrdinalIgnoreCase) || vfName.Contains("emby", StringComparison.OrdinalIgnoreCase) || loc.Contains("mediathek", StringComparison.OrdinalIgnoreCase) || loc.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                    score -= 80;
                try { if (Directory.GetDirectories(loc, "*Re*ZERO*", SearchOption.TopDirectoryOnly).Length > 0) score += 40; } catch { }
                candidates.Add((loc.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), score));
            }
        }

        return candidates.OrderByDescending(c => c.Score).Select(c => c.Path).FirstOrDefault();
    }

    internal static string ResolveShowDirectory(
        PluginConfiguration config,
        ILibraryManager libraryManager,
        string showFolderName,
        string? providerOverridePath,
        string storageRoot,
        string physicalSubfolder)
    {
        if (!string.IsNullOrWhiteSpace(providerOverridePath) && Directory.Exists(providerOverridePath))
        {
            var trimmed = providerOverridePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.EndsWith(showFolderName, StringComparison.OrdinalIgnoreCase) ? trimmed : Path.Combine(trimmed, showFolderName);
        }

        return Path.Combine(storageRoot, showFolderName);
    }

    internal static (string WriteDir, string? PerFileSymlinkDir) EnsureStorageLayout(
        string targetDir,
        string physicalDir,
        bool useSymlink,
        ILogger logger)
    {
        Directory.CreateDirectory(targetDir);
        return (targetDir, null);
    }

    internal static void EnsureFileSymlink(string linkPath, string targetFilePath, ILogger logger)
    {
        try
        {
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                var linkInfo = new FileInfo(linkPath);
                if (linkInfo.LinkTarget != null)
                {
                    var linkParent = Path.GetDirectoryName(linkPath) ?? string.Empty;
                    var resolvedExisting = Path.TrimEndingDirectorySeparator(Path.GetFullPath(linkInfo.LinkTarget, linkParent));
                    var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFilePath));
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (string.Equals(resolvedExisting, normalizedTarget, comparison))
                    {
                        return;
                    }

                    File.Delete(linkPath);
                }
                else
                {
                    return;
                }
            }

            var linkParentDir = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(linkParentDir))
            {
                Directory.CreateDirectory(linkParentDir);
            }

            File.CreateSymbolicLink(linkPath, targetFilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create file symlink {Link} -> {Target}", linkPath, targetFilePath);
        }
    }

    internal static async Task<bool> WriteIfChangedAsync(string filePath, string newContent, CancellationToken cancellationToken)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existing.Trim(), newContent.Trim(), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch
            {
                // In case of read collision, overwrite
            }
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, newContent, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static async Task DownloadArtworkAsync(
        System.Net.Http.HttpClient httpClient,
        string imageUrl,
        string destinationPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var response = await httpClient.GetAsync(imageUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Artwork download from {Url} failed with status {StatusCode}", imageUrl, response.StatusCode);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Successfully downloaded artwork to {Path}", destinationPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download artwork from {Url} to {Path}", imageUrl, destinationPath);
        }
    }

    internal static void TryDeleteOppositeStrm(string physicalDir, string? symlinkDir, string fileNameBase)
    {
        var physicalStrm = Path.Combine(physicalDir, $"{fileNameBase}.strm");
        if (File.Exists(physicalStrm))
        {
            try { File.Delete(physicalStrm); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(symlinkDir))
        {
            var symlinkStrm = Path.Combine(symlinkDir, $"{fileNameBase}.strm");
            if (File.Exists(symlinkStrm))
            {
                try { File.Delete(symlinkStrm); } catch { }
            }
        }
    }

    internal static string BuildTvShowNfo(
        string title,
        string plot,
        string? originalTitle = null,
        IEnumerable<string>? genres = null,
        string? studio = null,
        string? posterUrl = null,
        string? tvdbId = null) =>
        AnimeNfoBuilder.BuildTvShowNfo(title, plot, originalTitle, genres, studio, posterUrl, tvdbId);

    internal static string BuildEpisodeNfo(
        string title,
        string showTitle,
        int seasonNumber,
        int episodeNumber,
        string? plot,
        string? thumbUrl,
        string lang = "jpn",
        bool hasSubtitles = true,
        int? introStart = null,
        int? introEnd = null,
        int? outroStart = null,
        int? outroEnd = null,
        string? mangaChapters = null) =>
        AnimeNfoBuilder.BuildEpisodeNfo(title, showTitle, seasonNumber, episodeNumber, plot, thumbUrl, lang, hasSubtitles, introStart, introEnd, outroStart, outroEnd, mangaChapters);

    internal static string BuildSeasonNfo(string title, int seasonNumber, string? plot = null) =>
        AnimeNfoBuilder.BuildSeasonNfo(title, seasonNumber, plot);

    internal static async Task SanitizeSpecialsPlacementAsync(
        ILibraryManager libraryManager,
        string targetSeasonDir,
        string physicalSeasonDir,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            });

            if (items == null || items.Count == 0)
            {
                return;
            }

            var cleaned = 0;
            foreach (var baseItem in items)
            {
                if (baseItem is not Episode ep || string.IsNullOrWhiteSpace(ep.Path))
                {
                    continue;
                }

                var isTargetSpecial = ep.Path.StartsWith(targetSeasonDir, StringComparison.OrdinalIgnoreCase) ||
                                      ep.Path.StartsWith(physicalSeasonDir, StringComparison.OrdinalIgnoreCase) ||
                                      (ep.ParentIndexNumber == 0 && ep.Path.Contains("[Arc ", StringComparison.OrdinalIgnoreCase));

                if (!isTargetSpecial)
                {
                    continue;
                }

                string? desiredTitle = null;
                var nfoPath = Path.ChangeExtension(ep.Path, ".nfo");
                if (File.Exists(nfoPath))
                {
                    try
                    {
                        var doc = XDocument.Load(nfoPath);
                        desiredTitle = doc.Root?.Element("title")?.Value;
                    }
                    catch
                    {
                        // Ignore malformed NFO
                    }
                }

                var titleChanged = !string.IsNullOrWhiteSpace(desiredTitle) &&
                                   !string.Equals(ep.Name, desiredTitle, StringComparison.Ordinal);

                if (titleChanged ||
                    ep.AirsBeforeSeasonNumber.HasValue ||
                    ep.AirsAfterSeasonNumber.HasValue ||
                    ep.AirsBeforeEpisodeNumber.HasValue ||
                    !ep.IsLocked ||
                    (ep.ProviderIds != null && ep.ProviderIds.Count > 0))
                {
                    if (!string.IsNullOrWhiteSpace(desiredTitle))
                    {
                        ep.Name = desiredTitle;
                        ep.SortName = null;
                    }

                    ep.AirsBeforeSeasonNumber = null;
                    ep.AirsAfterSeasonNumber = null;
                    ep.AirsBeforeEpisodeNumber = null;
                    ep.OriginalTitle = ep.Name;
                    ep.ProviderIds?.Clear();
                    ep.IsLocked = true;
                    await libraryManager.UpdateItemAsync(ep, ep.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    cleaned++;
                }
            }

            if (cleaned > 0)
            {
                logger.LogInformation("Sanitized {Count} Season 00 episodes (removed TVDB mid-season injection flags and locked metadata).", cleaned);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sanitize Season 00 specials placement in Jellyfin database.");
        }
    }

    private static bool IsGeneratedPointerOrNfo(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".part", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrNestedPath(string path1, string path2)
    {
        try
        {
            var p1 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path1));
            var p2 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path2));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(p1, p2, comparison) ||
                   p1.StartsWith(p2 + Path.DirectorySeparatorChar, comparison) ||
                   p2.StartsWith(p1 + Path.DirectorySeparatorChar, comparison);
        }
        catch
        {
            return false;
        }
    }

}
