using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AnimeKai.Api;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Models;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Providers.Rezero;

/// <summary>
/// Provider implementation for Re:Zero - The Perfect Run fan re-cut.
/// Supports both fast adaptive HLS master streaming and direct HTTPS MP4 downloads.
/// </summary>
public class RezeroStreamProvider : IAnimeStreamProvider
{
    private static readonly Regex DirectMp4UrlRegex = new(@"/downloads/([^/]+)/([^/]+)/([^/?#]+)\.mp4", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HlsUrlRegex = new(@"/HLS/([^/]+)/([^/]+)/[^/]+/([^/]+)/index\.m3u8", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArcFileRegex = new(@"\[Arc\s*0?(\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpFileRegex = new(@"Episode\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpFileRegex = new(@"S\d+E(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IRezeroApiClient _apiClient;
    private readonly IRezeroDownloadService _downloadService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RezeroStreamProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroStreamProvider"/> class.
    /// </summary>
    public RezeroStreamProvider(
        IRezeroApiClient apiClient,
        IRezeroDownloadService downloadService,
        ILibraryManager libraryManager,
        ILogger<RezeroStreamProvider> logger)
    {
        _apiClient = apiClient;
        _downloadService = downloadService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "rezero";

    /// <inheritdoc />
    public string DisplayName => "Re:Zero - The Perfect Run";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableRezero;

    /// <inheritdoc />
    public async Task<ProviderSyncResult> SyncEpisodesAsync(SyncContext context, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = context.Configuration;
        _logger.LogInformation("Starting Re:Zero sync (Mode: {Mode}, Format: {Format})...", config.SyncMode, config.StreamFormat);

        var videosResponse = await _apiClient.GetArcsAsync(cancellationToken).ConfigureAwait(false);
        if (videosResponse?.Arcs == null || videosResponse.Arcs.Count == 0)
        {
            return new ProviderSyncResult(ProviderId, DisplayName, false, 0, "No Re:Zero arcs retrieved from upstream API.");
        }

        var showDir = ResolveShowDirectory(config, context.StorageRoot);
        var physicalSeasonDir = Path.Combine(context.StorageRoot, "ReZero", config.SeasonFolder);
        var targetSeasonDir = Path.Combine(showDir, config.SeasonFolder);

        var (writeDir, perFileSymlinkDir) = AnimeStorageLinker.EnsureStorageLayout(
            targetSeasonDir,
            physicalSeasonDir,
            context.UseSymlink,
            _logger);

        // Lock season.nfo so TVDB never maps Season 00 episodes to Break Time specials
        var seasonNfo = Path.Combine(writeDir, "season.nfo");
        var seasonNfoContent = AnimeStorageLinker.BuildSeasonNfo(
            "Re:Zero - The Perfect Run",
            config.SeasonNumber,
            "Chronological narrative fan re-cut of Re:Zero - Starting Life in Another World (Arcs 1-6).");
        await AnimeStorageLinker.WriteIfChangedAsync(seasonNfo, seasonNfoContent, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
        {
            AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, "season.nfo"), seasonNfo, _logger);
        }

        // tvshow.nfo if standalone
        if (string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase))
        {
            var tvshowNfo = Path.Combine(showDir, "tvshow.nfo");
            if (!File.Exists(tvshowNfo))
            {
                var content = AnimeStorageLinker.BuildTvShowNfo(
                    config.StandaloneSeriesTitle,
                    "A seamless chronological recut of Re:Zero - Starting Life in Another World without commercial breaks, duplicate scenes, or pacing bloat.",
                    "Re:Zero - The Perfect Run",
                    new[] { "Animation", "Anime", "Fantasy", "Drama", "Psychological", "Thriller" },
                    "White Fox / Perfect Run Team",
                    "https://rezeroperfect.run/juicy_prototype/images/arc1.jpg");
                await AnimeStorageLinker.WriteIfChangedAsync(tvshowNfo, content, cancellationToken).ConfigureAwait(false);
            }
        }

        var total = 0;
        var globalIndex = 1;
        var isDownload = string.Equals(config.SyncMode, "Download", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < videosResponse.Arcs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arc = videosResponse.Arcs[i];
            var arcEps = await _apiClient.GetArcEpisodesAsync(arc.Id, cancellationToken).ConfigureAwait(false);
            if (arcEps?.Episodes == null || arcEps.Episodes.Count == 0) continue;

            foreach (var ep in arcEps.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var epTitle = string.IsNullOrWhiteSpace(ep.Title) ? $"Episode {ep.Number}" : ep.Title;
                var fileNameBase = $"Re ZERO Starting Life in Another World - S{config.SeasonNumber:D2}E{globalIndex:D2} - [Arc {arc.Number:D2}] {Sanitize(epTitle)}";

                if (isDownload)
                {
                    var dlUrl = _apiClient.ResolveDownloadUrl(ep, arc, "jp", "cc");
                    if (string.IsNullOrWhiteSpace(dlUrl))
                    {
                        continue;
                    }

                    var destMp4 = Path.Combine(writeDir, $"{fileNameBase}.mp4");
                    var dlResult = await _downloadService.DownloadEpisodeAsync(dlUrl, destMp4, config.SkipExistingDownloads, cancellationToken).ConfigureAwait(false);
                    if (dlResult.Succeeded && config.CleanOppositeModeFiles)
                    {
                        AnimeStorageLinker.TryDeleteOppositeStrm(writeDir, perFileSymlinkDir, fileNameBase);
                    }
                }
                else
                {
                    var streamUrl = _apiClient.ResolveStreamUrl(ep, arc, config.PreferredLanguage, config.PreferredCaptions, config.PreferredQuality, config.StreamFormat);
                    if (string.IsNullOrWhiteSpace(streamUrl))
                    {
                        continue;
                    }

                    var strmFile = Path.Combine(writeDir, $"{fileNameBase}.strm");
                    await AnimeStorageLinker.WriteIfChangedAsync(strmFile, streamUrl, cancellationToken).ConfigureAwait(false);
                }

                var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");
                var epNfo = AnimeStorageLinker.BuildEpisodeNfo(
                    $"{arc.Title} - {epTitle} - [The Perfect Run] Arc {arc.Number:D2}",
                    "Re: ZERO, Starting Life in Another World",
                    config.SeasonNumber,
                    globalIndex,
                    arc.Description,
                    !string.IsNullOrWhiteSpace(arc.WatchArtwork) ? arc.WatchArtwork : arc.Artwork,
                    config.PreferredLanguage.Contains("en") ? "eng" : "jpn",
                    !config.PreferredCaptions.Equals("clean", StringComparison.OrdinalIgnoreCase),
                    ep.Segments?.Intro?.Start,
                    ep.Segments?.Intro?.End,
                    ep.Segments?.Outro?.Start,
                    ep.Segments?.Outro?.End);

                await AnimeStorageLinker.WriteIfChangedAsync(nfoFile, epNfo, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
                {
                    var ext = isDownload ? ".mp4" : ".strm";
                    var mediaTarget = isDownload ? Path.Combine(writeDir, $"{fileNameBase}.mp4") : Path.Combine(writeDir, $"{fileNameBase}.strm");
                    AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}{ext}"), mediaTarget, _logger);
                    AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.nfo"), nfoFile, _logger);
                }

                total++;
                globalIndex++;
            }

            progress?.Report((double)(i + 1) / videosResponse.Arcs.Count * 100.0);
        }

        await AnimeStorageLinker.SanitizeSpecialsPlacementAsync(_libraryManager, targetSeasonDir, physicalSeasonDir, _logger, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Re:Zero sync completed: {Count} episodes.", total);
        return new ProviderSyncResult(ProviderId, DisplayName, true, total, $"Synchronized {total} Re:Zero episodes.");
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode || string.IsNullOrWhiteSpace(item.Path) || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var path = item.Path;

        var isRezero = path.Contains("Re - ZERO", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("Re:Zero", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("Re ZERO", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("/rezero/", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("\\rezero\\", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("[The Perfect Run]", StringComparison.OrdinalIgnoreCase);

        string? strmContent = null;
        if (File.Exists(path) && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                strmContent = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
                if (!isRezero && strmContent.Contains("rezeroperfect.run", StringComparison.OrdinalIgnoreCase))
                {
                    isRezero = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect .strm file at {Path}", path);
            }
        }

        if (!isRezero)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var (arcPart, epPart, basePrefix) = ResolveStreamParts(path, strmContent);
        if (string.IsNullOrWhiteSpace(arcPart) || string.IsNullOrWhiteSpace(epPart) || string.IsNullOrWhiteSpace(basePrefix))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var variants = new[]
        {
            new { Key = "jp_cc", Name = "Japanese (English Subtitles)", Suffix = "JP BURNED CC", Lang = "jpn", AudioTitle = "Japanese 2.0", HasSubtitles = true },
            new { Key = "jp", Name = "Japanese (Clean / No Subtitles)", Suffix = "JP", Lang = "jpn", AudioTitle = "Japanese 2.0", HasSubtitles = false },
            new { Key = "en", Name = "English Dub (Clean / No Subtitles)", Suffix = "EN", Lang = "eng", AudioTitle = "English 2.0", HasSubtitles = false },
            new { Key = "en_cc", Name = "English Dub (Burned CC)", Suffix = "EN BURNED CC", Lang = "eng", AudioTitle = "English 2.0", HasSubtitles = true }
        };

        var sources = new List<MediaSourceInfo>();
        var hlsBase = EnsureTrailingSlash(config.HlsBaseUrl, "https://media.rezeroperfect.run/HLS/");
        var downloadsBase = EnsureTrailingSlash(config.DownloadsBaseUrl, "https://media.rezeroperfect.run/downloads/");
        var isDirectMp4Preferred = string.Equals(config.StreamFormat, "direct_mp4", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < variants.Length; i++)
        {
            var v = variants[i];
            var streamName = $"{basePrefix} - {v.Suffix}";
            var escapedStreamName = Uri.EscapeDataString(streamName);

            string streamUrl;
            string container;

            if (isDirectMp4Preferred)
            {
                streamUrl = $"{downloadsBase}{arcPart}/{epPart}/{escapedStreamName}.mp4";
                container = "mp4";
            }
            else
            {
                streamUrl = $"{hlsBase}{arcPart}/{epPart}/auto/{escapedStreamName}/index.m3u8";
                container = "m3u8";
            }

            var mediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = 1920,
                    Height = 1080,
                    BitRate = 8000000,
                    IsDefault = true
                },
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    Language = v.Lang,
                    Title = v.AudioTitle,
                    IsDefault = true
                }
            };

            if (v.HasSubtitles)
            {
                mediaStreams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = 2,
                    Codec = "subrip",
                    Language = "eng",
                    Title = "English (Burned In)",
                    IsDefault = true,
                    IsForced = true
                });
            }

            var sourceInfo = new MediaSourceInfo
            {
                Id = i == 0 ? item.Id.ToString("N") : $"{item.Id:N}_{v.Key}",
                Name = v.Name,
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = container,
                Bitrate = 8000000,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                DefaultAudioStreamIndex = 1,
                DefaultSubtitleStreamIndex = v.HasSubtitles ? 2 : null,
                RunTimeTicks = item.RunTimeTicks,
                MediaStreams = mediaStreams
            };

            sources.Add(sourceInfo);
        }

        return sources;
    }

    private static (string ArcPart, string EpPart, string BasePrefix) ResolveStreamParts(
        string path,
        string? strmContent)
    {
        if (!string.IsNullOrWhiteSpace(strmContent))
        {
            var urlMatch = DirectMp4UrlRegex.Match(strmContent);
            if (!urlMatch.Success)
            {
                urlMatch = HlsUrlRegex.Match(strmContent);
            }

            if (urlMatch.Success)
            {
                var arcPart = urlMatch.Groups[1].Value;
                var epPart = urlMatch.Groups[2].Value;
                var rawStreamName = urlMatch.Groups[3].Value;
                var decodedName = Uri.UnescapeDataString(rawStreamName);
                var basePrefix = StripVariantSuffix(decodedName);
                return (arcPart, epPart, basePrefix);
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var isMemorySnow = fileName.Contains("Memory Snow", StringComparison.OrdinalIgnoreCase);

        var arcMatch = ArcFileRegex.Match(fileName);
        var epMatch = EpFileRegex.Match(fileName);
        if (!epMatch.Success)
        {
            epMatch = SeasonEpFileRegex.Match(fileName);
        }

        var arcNum = arcMatch.Success && int.TryParse(arcMatch.Groups[1].Value, out var a) ? a : (isMemorySnow ? 2 : 1);
        var epNum = epMatch.Success && int.TryParse(epMatch.Groups[1].Value, out var e) ? e : 1;

        var parsedArcPart = $"ARC%20{arcNum:D2}";
        if (isMemorySnow)
        {
            return (parsedArcPart, "Memory%20Snow", "ARC 02 - Memory Snow");
        }

        var parsedEpPart = $"EP%20{epNum}";
        var parsedBasePrefix = $"ARC {arcNum:D2} - EP {epNum}";
        return (parsedArcPart, parsedEpPart, parsedBasePrefix);
    }

    private static string StripVariantSuffix(string name)
    {
        var suffixes = new[] { " - JP BURNED CC", " - JP", " - EN BURNED CC", " - EN" };
        foreach (var s in suffixes)
        {
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(0, name.Length - s.Length);
            }
        }

        return name;
    }

    private static string EnsureTrailingSlash(string? url, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
        return value.EndsWith('/') ? value : value + "/";
    }

    private string ResolveShowDirectory(PluginConfiguration config, string storageRoot)
    {
        if (string.Equals(config.IntegrationMode, "StandaloneSeries", StringComparison.OrdinalIgnoreCase))
        {
            var title = string.IsNullOrWhiteSpace(config.StandaloneSeriesTitle) ? "ReZero - The Perfect Run" : Sanitize(config.StandaloneSeriesTitle);
            return AnimeStorageLinker.ResolveShowDirectory(config, _libraryManager, title, config.StandaloneLibraryPath, storageRoot, "ReZero");
        }

        if (!string.IsNullOrWhiteSpace(config.TargetSeriesPath) && Directory.Exists(config.TargetSeriesPath))
        {
            return config.TargetSeriesPath;
        }

        var primaryRoot = AnimeStorageLinker.ResolveTargetTvLibraryRoot(config, _libraryManager);
        if (!string.IsNullOrWhiteSpace(primaryRoot) && Directory.Exists(primaryRoot))
        {
            var primaryCandidates = Directory.GetDirectories(primaryRoot, "*Re*ZERO*", SearchOption.TopDirectoryOnly);
            if (primaryCandidates.Length > 0)
            {
                return primaryCandidates[0];
            }
        }

        var virtualFolders = _libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
        var tvFolders = virtualFolders.Where(f => string.Equals(f.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.Locations).ToList();
        foreach (var folder in tvFolders)
        {
            if (!Directory.Exists(folder)) continue;
            var candidates = Directory.GetDirectories(folder, "*Re*ZERO*", SearchOption.TopDirectoryOnly);
            if (candidates.Length > 0)
            {
                return candidates[0];
            }
        }

        return Path.Combine(storageRoot, "ReZero");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
