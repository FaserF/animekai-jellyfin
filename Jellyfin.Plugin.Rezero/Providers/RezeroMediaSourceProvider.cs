using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Exposes fast adaptive HLS streams and multi-language / multi-caption versions
/// (Japanese + English Subtitles, Clean Japanese, English Dub, English Dub + CC)
/// directly in Jellyfin client players without requiring duplicated files on disk.
/// </summary>
public class RezeroMediaSourceProvider : IMediaSourceProvider, IHasOrder
{
    private static readonly Regex DirectMp4UrlRegex = new(@"/downloads/([^/]+)/([^/]+)/([^/?#]+)\.mp4", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HlsUrlRegex = new(@"/HLS/([^/]+)/([^/]+)/[^/]+/([^/]+)/index\.m3u8", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArcFileRegex = new(@"\[Arc\s*0?(\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpFileRegex = new(@"Episode\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpFileRegex = new(@"S\d+E(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        if (item is not Episode)
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
            new { Key = "en_cc", Name = "English Dub (English Subtitles)", Suffix = "EN BURNED CC", Lang = "eng", AudioTitle = "English 2.0", HasSubtitles = true }
        };

        var prefLang = (config.PreferredLanguage ?? "jp").ToLowerInvariant();
        var prefCaps = (config.PreferredCaptions ?? "cc").ToLowerInvariant();
        var preferredKey = prefLang switch
        {
            "en" => prefCaps == "off" ? "en" : "en_cc",
            _ => prefCaps == "off" ? "jp" : "jp_cc"
        };

        var sortedVariants = variants.OrderBy(v => v.Key == preferredKey ? 0 : 1).ToList();

        var hlsBase = EnsureTrailingSlash(config.HlsBaseUrl, "https://media.rezeroperfect.run/HLS/");
        var downloadsBase = EnsureTrailingSlash(config.DownloadsBaseUrl, "https://media.rezeroperfect.run/downloads/");
        var isDirectMp4 = string.Equals(config.StreamFormat, "direct_mp4", StringComparison.OrdinalIgnoreCase);

        var sources = new List<MediaSourceInfo>();
        for (var i = 0; i < sortedVariants.Count; i++)
        {
            var v = sortedVariants[i];
            var streamName = $"{basePrefix} - {v.Suffix}";
            var escapedStreamName = Uri.EscapeDataString(streamName);

            string streamUrl;
            string container;
            if (isDirectMp4)
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

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveStream>(null!);

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
}
