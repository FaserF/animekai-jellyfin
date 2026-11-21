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

        var libRoot = ResolveTargetTvLibraryRoot(config, libraryManager);
        return !string.IsNullOrWhiteSpace(libRoot) ? Path.Combine(libRoot, showFolderName) : Path.Combine(storageRoot, physicalSubfolder);
    }
    internal static (string WriteDir, string? PerFileSymlinkDir) EnsureStorageLayout(
        string targetDir,
        string physicalDir,
        bool useSymlink,
        ILogger logger)
    {
        if (!useSymlink || IsSameOrNestedPath(targetDir, physicalDir))
        {
            Directory.CreateDirectory(targetDir);
            return (targetDir, null);
        }

        Directory.CreateDirectory(physicalDir);

        try
        {
            var targetInfo = new DirectoryInfo(targetDir);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (targetInfo.LinkTarget != null)
            {
                var targetParent = Path.GetDirectoryName(targetDir) ?? Path.GetPathRoot(targetDir) ?? string.Empty;
                var resolvedExisting = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetInfo.LinkTarget, targetParent));
                var normalizedPhysical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalDir));
                if (string.Equals(resolvedExisting, normalizedPhysical, comparison))
                {
                    return (physicalDir, null);
                }

                Directory.Delete(targetDir, recursive: false);
                Directory.CreateSymbolicLink(targetDir, physicalDir);
                logger.LogInformation("Updated directory symlink {Target} -> {Physical}", targetDir, physicalDir);
                return (physicalDir, null);
            }

            if (!Directory.Exists(targetDir) && !File.Exists(targetDir))
            {
                Directory.CreateSymbolicLink(targetDir, physicalDir);
                logger.LogInformation("Created directory symlink {Target} -> {Physical}", targetDir, physicalDir);
                return (physicalDir, null);
            }

            if (Directory.Exists(targetDir))
            {
                var entries = Directory.EnumerateFileSystemEntries(targetDir).ToList();
                if (entries.Count == 0)
                {
                    Directory.Delete(targetDir, recursive: false);
                    Directory.CreateSymbolicLink(targetDir, physicalDir);
                    logger.LogInformation("Replaced empty directory with symlink {Target} -> {Physical}", targetDir, physicalDir);
                    return (physicalDir, null);
                }

                if (entries.All(e => !Directory.Exists(e) && IsGeneratedPointerOrNfo(e)))
                {
                    foreach (var entry in entries)
                    {
                        File.Delete(entry);
                    }

                    Directory.Delete(targetDir, recursive: false);
                    Directory.CreateSymbolicLink(targetDir, physicalDir);
                    logger.LogInformation("Migrated existing stream folder to symlink {Target} -> {Physical}", targetDir, physicalDir);
                    return (physicalDir, null);
                }

                logger.LogInformation("Existing files preserved in {Target}; linking files individually from {Physical}", targetDir, physicalDir);
                return (physicalDir, targetDir);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create symlink {Target} -> {Physical}; falling back to direct writes.", targetDir, physicalDir);
        }

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
        string? tvdbId = null)
    {
        var elements = new List<object>
        {
            new XElement("title", title),
            new XElement("originaltitle", originalTitle ?? title),
            new XElement("showtitle", title),
            new XElement("plot", plot),
            new XElement("studio", studio ?? "Anime Kai & Fan-Edits"),
            new XElement("status", "Continuing"),
            new XElement("lockdata", "true")
        };

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            elements.Add(new XElement("uniqueid", new XAttribute("type", "tvdb"), new XAttribute("default", "true"), tvdbId));
            elements.Add(new XElement("tvdbid", tvdbId));
        }

        if (genres != null)
        {
            foreach (var g in genres)
            {
                elements.Add(new XElement("genre", g));
            }
        }
        else
        {
            elements.Add(new XElement("genre", "Animation"));
            elements.Add(new XElement("genre", "Anime"));
            elements.Add(new XElement("genre", "Action"));
        }

        if (IsValidImageUrl(posterUrl))
        {
            elements.Add(new XElement("thumb", new XAttribute("aspect", "poster"), posterUrl));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("tvshow", elements));

        return doc.Declaration + Environment.NewLine + doc;
    }

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
        string? mangaChapters = null)
    {
        var streamDetails = new XElement("streamdetails",
            new XElement("video",
                new XElement("codec", "h264"),
                new XElement("width", "1920"),
                new XElement("height", "1080"),
                new XElement("aspect", "16:9")),
            new XElement("audio",
                new XElement("codec", "aac"),
                new XElement("language", lang),
                new XElement("channels", "2")));

        if (hasSubtitles)
        {
            streamDetails.Add(new XElement("subtitle",
                new XElement("language", "eng"),
                new XElement("forced", "true")));
        }

        var elements = new List<object>
        {
            new XElement("title", title),
            new XElement("originaltitle", title),
            new XElement("showtitle", showTitle),
            new XElement("season", seasonNumber),
            new XElement("episode", episodeNumber),
            new XElement("plot", plot ?? string.Empty),
            new XElement("lockdata", "true")
        };

        if (!string.IsNullOrWhiteSpace(mangaChapters))
        {
            elements.Add(new XElement("tag", $"Manga Chapters: {mangaChapters}"));
        }

        if (IsValidImageUrl(thumbUrl))
        {
            elements.Add(new XElement("thumb", thumbUrl));
        }

        if (introStart.HasValue && introEnd.HasValue)
        {
            elements.Add(new XElement("intro",
                new XElement("start", introStart.Value),
                new XElement("end", introEnd.Value)));
        }

        if (outroStart.HasValue && outroEnd.HasValue)
        {
            elements.Add(new XElement("outro",
                new XElement("start", outroStart.Value),
                new XElement("end", outroEnd.Value)));
        }

        elements.Add(new XElement("fileinfo", streamDetails));

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("episodedetails", elements));

        return doc.Declaration + Environment.NewLine + doc;
    }

    internal static string BuildSeasonNfo(string title, int seasonNumber, string? plot = null)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("season",
                new XElement("title", title),
                new XElement("seasonnumber", seasonNumber),
                new XElement("plot", plot ?? string.Empty),
                new XElement("lockdata", "true")));
        return doc.Declaration + Environment.NewLine + doc;
    }

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

    private static bool IsValidImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (url.Contains("archive.org/download/") ||
            url.Contains("onepace.net/images/"))
        {
            return false;
        }

        return true;
    }
}
