using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.Providers;

/// <summary>
/// Dynamic MediaSource provider for Re:Zero Perfect Run episodes.
/// Enables in-player quality switching (1080p, 720p, 480p) across all Jellyfin client apps
/// without needing duplicate files on disk.
/// </summary>
public class RezeroMediaSourceProvider : IMediaSourceProvider, IHasOrder
{
    private static readonly Regex DirectMp4UrlRegex = new(@"/downloads/([^/]+)/([^/]+)/([^/?#]+)\.mp4", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HlsUrlRegex = new(@"/HLS/([^/]+)/([^/]+)/[^/]+/([^/]+)/index\.m3u8", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArcFileRegex = new(@"\[Arc\s*0?(\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpFileRegex = new(@"Episode\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpFileRegex = new(@"S\d+E(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BracketEnRegex = new(@"\[(?:[^\]]*\b)?EN(?:\b[^\]]*)?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BracketCleanRegex = new(@"\[(?:[^\]]*\b)?Clean(?:\b[^\]]*)?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WordEnRegex = new(@"\bEN\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CcRegex = new(@"\bCC\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<RezeroMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroMediaSourceProvider"/> class.
    /// </summary>
    public RezeroMediaSourceProvider(ILogger<RezeroMediaSourceProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => -50;

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode episode)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.OfferAllQualitiesInPlayer)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var path = item.Path;
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // Check if file is related to Re:Zero Perfect Run
        var isRezero = path.Contains("Re - ZERO", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("Re:Zero", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("Re ZERO", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("/rezero/", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("\\rezero\\", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("[The Perfect Run]", StringComparison.OrdinalIgnoreCase);

        string? strmContent = null;
        if (File.Exists(path))
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

        var (arcPart, epPart, streamName, lang, captions) = ResolveStreamParts(path, strmContent);
        if (string.IsNullOrWhiteSpace(arcPart) || string.IsNullOrWhiteSpace(epPart) || string.IsNullOrWhiteSpace(streamName))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var sources = new List<MediaSourceInfo>();
        var qualities = new[]
        {
            new { Name = "1080p (Full HD)", Code = "1080p", Width = 1920, Height = 1080, Bitrate = 8000000 },
            new { Name = "720p (HD)", Code = "720p", Width = 1280, Height = 720, Bitrate = 4000000 },
            new { Name = "480p (SD)", Code = "480p", Width = 854, Height = 480, Bitrate = 1500000 }
        };

        var hlsBase = config.HlsBaseUrl.TrimEnd('/') + "/";
        var downloadsBase = config.DownloadsBaseUrl.TrimEnd('/') + "/";
        var isDirectMp4Preferred = string.Equals(config.StreamFormat, "direct_mp4", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < qualities.Length; i++)
        {
            var q = qualities[i];
            string streamUrl;
            string container;

            if (i == 0 && isDirectMp4Preferred)
            {
                // 1080p Direct MP4 download stream
                streamUrl = $"{downloadsBase}{arcPart}/{epPart}/{streamName}.mp4";
                container = "mp4";
            }
            else
            {
                // HLS adaptive / multi-quality stream
                streamUrl = $"{hlsBase}{arcPart}/{epPart}/{q.Code}/{streamName}/index.m3u8";
                container = "m3u8";
            }

            var mediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = q.Width,
                    Height = q.Height,
                    BitRate = q.Bitrate,
                    IsDefault = i == 0
                },
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    Language = lang,
                    Title = lang == "en" ? "English 2.0" : "Japanese 2.0",
                    IsDefault = true
                }
            };

            var sourceInfo = new MediaSourceInfo
            {
                Id = $"{item.Id:N}_{q.Code}",
                Name = q.Name,
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = container,
                Bitrate = q.Bitrate,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                DefaultAudioStreamIndex = 1,
                RunTimeTicks = item.RunTimeTicks,
                MediaStreams = mediaStreams
            };

            sources.Add(sourceInfo);
        }

        return sources;
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveStream>(null!);

    private static (string ArcPart, string EpPart, string StreamName, string Lang, string Captions) ResolveStreamParts(
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
                var streamName = urlMatch.Groups[3].Value;
                var decodedName = Uri.UnescapeDataString(streamName);
                var lang = WordEnRegex.IsMatch(decodedName) ? "en" : "jp";
                var captions = CcRegex.IsMatch(decodedName) ? "cc" : "off";
                return (arcPart, epPart, streamName, lang, captions);
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var arcMatch = ArcFileRegex.Match(fileName);
        var epMatch = EpFileRegex.Match(fileName);
        if (!epMatch.Success)
        {
            epMatch = SeasonEpFileRegex.Match(fileName);
        }

        if (!arcMatch.Success && !epMatch.Success)
        {
            return (string.Empty, string.Empty, string.Empty, "jp", "cc");
        }

        var arcNum = arcMatch.Success && int.TryParse(arcMatch.Groups[1].Value, out var a) ? a : 1;
        var epNum = epMatch.Success && int.TryParse(epMatch.Groups[1].Value, out var e) ? e : 1;

        var fallbackLang = BracketEnRegex.IsMatch(fileName) ? "en" : "jp";
        var fallbackCaptions = BracketCleanRegex.IsMatch(fileName) ? "off" : "cc";

        var parsedArcPart = $"ARC%20{arcNum:D2}";
        var parsedEpPart = $"EP%20{epNum}";
        var tag = fallbackCaptions == "cc" ? $"{fallbackLang.ToUpperInvariant()}%20BURNED%20CC" : fallbackLang.ToUpperInvariant();
        var parsedStreamName = $"ARC%20{arcNum:D2}%20-%20EP%20{epNum}%20-%20{tag}";

        return (parsedArcPart, parsedEpPart, parsedStreamName, fallbackLang, fallbackCaptions);
    }
}
