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
    private static readonly Regex ArcEpRegex = new(@"\[Arc\s*0?(\d+)\]\s*(.+?)(?:\s*\[(.*?)\])?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpRegex = new(@"S\d+E(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                       path.Contains("[The Perfect Run]", StringComparison.OrdinalIgnoreCase);

        if (!isRezero && File.Exists(path))
        {
            try
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                isRezero = content.Contains("rezeroperfect.run", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Ignore I/O read errors
            }
        }

        if (!isRezero)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var (arcNumber, epNumber, lang, captions) = ParseEpisodeDetails(path);
        if (arcNumber <= 0 || epNumber <= 0)
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

        var arcPart = $"ARC%20{arcNumber:D2}";
        var epPart = $"EP%20{epNumber}";
        var tag = captions == "cc" ? $"{lang.ToUpperInvariant()}%20BURNED%20CC" : lang.ToUpperInvariant();
        var streamName = $"ARC%20{arcNumber:D2}%20-%20EP%20{epNumber}%20-%20{tag}";

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

    private static (int ArcNumber, int EpNumber, string Lang, string Captions) ParseEpisodeDetails(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var arcMatch = Regex.Match(fileName, @"\[Arc\s*0?(\d+)\]", RegexOptions.IgnoreCase);
        var arcNum = arcMatch.Success && int.TryParse(arcMatch.Groups[1].Value, out var a) ? a : 1;

        var epMatch = Regex.Match(fileName, @"Episode\s*(\d+)", RegexOptions.IgnoreCase);
        var epNum = epMatch.Success && int.TryParse(epMatch.Groups[1].Value, out var e) ? e : 1;

        var lang = fileName.Contains("EN", StringComparison.OrdinalIgnoreCase) ? "en" : "jp";
        var captions = fileName.Contains("Clean", StringComparison.OrdinalIgnoreCase) ? "off" : "cc";

        return (arcNum, epNum, lang, captions);
    }
}
