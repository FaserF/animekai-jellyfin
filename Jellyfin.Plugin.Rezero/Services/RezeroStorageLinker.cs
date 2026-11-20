using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Rezero.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.Services;

/// <summary>
/// Manages symlink storage layouts, NFO metadata serialization, and atomic file updates.
/// </summary>
internal static class RezeroStorageLinker
{
    internal static (string WriteDir, string? PerFileSymlinkDir) EnsureStorageLayout(
        StorageResolutionResult resolution,
        ILogger logger)
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
                logger.LogInformation("Updated directory symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                return (physicalDir, null);
            }

            if (!Directory.Exists(targetSeasonDir) && !File.Exists(targetSeasonDir))
            {
                Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                logger.LogInformation("Created directory symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                return (physicalDir, null);
            }

            if (Directory.Exists(targetSeasonDir))
            {
                var entries = Directory.EnumerateFileSystemEntries(targetSeasonDir).ToList();
                if (entries.Count == 0)
                {
                    Directory.Delete(targetSeasonDir, recursive: false);
                    Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                    logger.LogInformation("Replaced empty directory with symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                    return (physicalDir, null);
                }

                if (entries.All(e => !Directory.Exists(e) && IsGeneratedPointerOrNfo(e)))
                {
                    foreach (var entry in entries)
                    {
                        File.Delete(entry);
                    }

                    Directory.Delete(targetSeasonDir, recursive: false);
                    Directory.CreateSymbolicLink(targetSeasonDir, physicalDir);
                    logger.LogInformation("Migrated existing Re:Zero stream folder to symlink {Target} -> {Physical}", targetSeasonDir, physicalDir);
                    return (physicalDir, null);
                }

                logger.LogInformation(
                    "Existing files preserved in {Target}; linking Re:Zero files individually from {Physical}",
                    targetSeasonDir,
                    physicalDir);
                return (physicalDir, targetSeasonDir);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create symlink {Target} -> {Physical}; falling back to direct writes.", targetSeasonDir, physicalDir);
        }

        Directory.CreateDirectory(targetSeasonDir);
        return (targetSeasonDir, null);
    }

    internal static void EnsureFileSymlink(string linkPath, string targetFilePath, ILogger logger)
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
                if (!IsGeneratedPointerOrNfo(linkPath))
                {
                    return;
                }

                File.Delete(linkPath);
            }

            File.CreateSymbolicLink(linkPath, targetFilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create per-file symlink {Link} -> {Target}", linkPath, targetFilePath);
        }
    }

    internal static void TryDeleteOppositeStrm(string writeDir, string? perFileSymlinkDir, string fileNameBase)
    {
        var strmName = $"{fileNameBase}.strm";
        DeleteIfExists(Path.Combine(writeDir, strmName));
        if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
        {
            DeleteIfExists(Path.Combine(perFileSymlinkDir, strmName));
        }
    }

    internal static bool IsGeneratedPointerOrNfo(string filePath)
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

    internal static async Task WriteIfChangedAsync(string filePath, string newContent, CancellationToken cancellationToken)
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

    internal static string BuildEpisodeNfo(
        ArcDto arc,
        EpisodeDto episode,
        int globalEpisodeIndex,
        int seasonNumber,
        string showTitle,
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

    internal static string BuildTvShowNfo(string title)
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

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path) || new FileInfo(path).LinkTarget != null)
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Ignore cleanup failure for stale .strm pointers
        }
    }
}
