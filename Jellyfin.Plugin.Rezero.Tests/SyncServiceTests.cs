using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Configuration;
using Jellyfin.Plugin.Rezero.Models;
using Jellyfin.Plugin.Rezero.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Rezero.Tests;

public class SyncServiceTests
{
    private static Mock<IRezeroApiClient> CreateMockApi(bool twoEpisodes = false)
    {
        var mockApi = new Mock<IRezeroApiClient>();
        mockApi.Setup(a => a.GetArcsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideosResponse
            {
                Arcs = new List<ArcDto>
                {
                    new()
                    {
                        Id = "arc01",
                        Number = 1,
                        Title = "First Day",
                        Description = "First arc description.",
                        Artwork = "/images/arc1.jpg"
                    }
                }
            });

        var episodes = new List<EpisodeDto>
        {
            new()
            {
                Number = 1,
                Title = "Arrival",
                Sources = new()
                {
                    ["jp"] = new()
                    {
                        ["cc"] = new() { ["1080p"] = "ARC%2001/EP%201/1080p/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC/index.m3u8" },
                        ["off"] = new() { ["1080p"] = "ARC%2001/EP%201/1080p/ARC%2001%20-%20EP%201%20-%20JP/index.m3u8" }
                    },
                    ["en"] = new()
                    {
                        ["cc"] = new() { ["1080p"] = "ARC%2001/EP%201/1080p/ARC%2001%20-%20EP%201%20-%20EN%20BURNED%20CC/index.m3u8" },
                        ["off"] = new() { ["1080p"] = "ARC%2001/EP%201/1080p/ARC%2001%20-%20EP%201%20-%20EN/index.m3u8" }
                    }
                },
                Segments = new EpisodeSegments
                {
                    Intro = new SegmentRange { Start = 60, End = 150 },
                    Outro = new SegmentRange { Start = 1200, End = 1290 }
                }
            }
        };

        if (twoEpisodes)
        {
            episodes.Add(new EpisodeDto
            {
                Number = 2,
                Title = "Escape",
                Sources = new()
                {
                    ["jp"] = new()
                    {
                        ["cc"] = new() { ["1080p"] = "ARC%2001/EP%202/1080p/ARC%2001%20-%20EP%202%20-%20JP%20BURNED%20CC/index.m3u8" }
                    }
                }
            });
        }

        mockApi.Setup(a => a.GetArcEpisodesAsync("arc01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeDataResponse { Episodes = episodes });

        mockApi.Setup(a => a.GetUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdatesResponse());

        mockApi.Setup(a => a.ResolveStreamUrl(It.IsAny<EpisodeDto>(), It.IsAny<ArcDto>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((EpisodeDto ep, ArcDto arc, string lang, string cap, string quality, string format) =>
            {
                var tag = cap == "cc" ? $"{lang.ToUpperInvariant()}%20BURNED%20CC" : lang.ToUpperInvariant();
                if (format == "hls_m3u8")
                {
                    return $"https://media.rezeroperfect.run/HLS/ARC%20{arc.Number:D2}/EP%20{ep.Number}/{quality}/ARC%20{arc.Number:D2}%20-%20EP%20{ep.Number}%20-%20{tag}/index.m3u8";
                }

                return $"https://media.rezeroperfect.run/downloads/ARC%20{arc.Number:D2}/EP%20{ep.Number}/ARC%20{arc.Number:D2}%20-%20EP%20{ep.Number}%20-%20{tag}.mp4";
            });

        mockApi.Setup(a => a.ResolveDownloadUrl(It.IsAny<EpisodeDto>(), It.IsAny<ArcDto>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((EpisodeDto ep, ArcDto arc, string lang, string cap) =>
            {
                var tag = cap == "cc" ? $"{lang.ToUpperInvariant()}%20BURNED%20CC" : lang.ToUpperInvariant();
                return $"https://media.rezeroperfect.run/downloads/ARC%20{arc.Number:D2}/EP%20{ep.Number}/ARC%20{arc.Number:D2}%20-%20EP%20{ep.Number}%20-%20{tag}.mp4";
            });

        return mockApi;
    }

    private static StoragePathResolver CreateMockResolver() => new(
        getMounts: () => Array.Empty<MountEntry>(),
        isDirectoryWritable: _ => true,
        isNetworkPath: _ => false);

    [Fact]
    public async Task SyncEpisodesAsync_ShouldWriteExpectedStrmAndNfoFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "RezeroTest_" + Guid.NewGuid().ToString("N"));
        var seriesDir = Path.Combine(tempRoot, "tv", "Re - ZERO");
        var dedicatedDir = Path.Combine(tempRoot, "rezero", "Season 00");
        Directory.CreateDirectory(seriesDir);

        try
        {
            var mockApi = CreateMockApi();
            var config = new PluginConfiguration
            {
                TargetSeriesPath = seriesDir,
                DedicatedStoragePath = dedicatedDir,
                EnableSymlink = true,
                EnableMultiVersionFiles = false
            };

            var service = new RezeroSyncService(
                mockApi.Object,
                Mock.Of<IRezeroDownloadService>(),
                NullLogger<RezeroSyncService>.Instance,
                null,
                null,
                CreateMockResolver(),
                config);

            var count = await service.SyncEpisodesAsync(ct);
            count.Should().Be(1);

            var seasonDir = Path.Combine(seriesDir, "Season 00");
            Directory.Exists(seasonDir).Should().BeTrue();

            var strmFiles = Directory.GetFiles(seasonDir, "*.strm");
            var nfoFiles = Directory.GetFiles(seasonDir, "*.nfo");

            strmFiles.Should().HaveCount(1);
            nfoFiles.Should().HaveCount(1);

            var strmContent = await File.ReadAllTextAsync(strmFiles[0], ct);
            strmContent.Trim().Should().Be("https://media.rezeroperfect.run/downloads/ARC%2001/EP%201/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC.mp4");

            var nfoContent = await File.ReadAllTextAsync(nfoFiles[0], ct);
            nfoContent.Should().Contain("<intro>");
            nfoContent.Should().Contain("<start>60</start>");
            nfoContent.Should().Contain("<outro>");
            nfoContent.Should().Contain("<end>1290</end>");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task SyncEpisodesAsync_ShouldPreserveExistingNonRezeroUserFilesAndExistingMp4InStreamMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "RezeroTest_" + Guid.NewGuid().ToString("N"));
        var seriesDir = Path.Combine(tempRoot, "tv", "Re - ZERO");
        var seasonDir = Path.Combine(seriesDir, "Season 00");
        var dedicatedDir = Path.Combine(tempRoot, "rezero", "Season 00");
        Directory.CreateDirectory(seasonDir);
        var ovaFile = Path.Combine(seasonDir, "Memory Snow.mkv");
        var downloadedMp4 = Path.Combine(seasonDir, "Re ZERO Starting Life in Another World - S00E01 - [Arc 01] Arrival.mp4");
        await File.WriteAllTextAsync(ovaFile, "ova-bytes", ct);
        await File.WriteAllTextAsync(downloadedMp4, "mp4-bytes", ct);

        try
        {
            var mockApi = CreateMockApi();
            var config = new PluginConfiguration
            {
                TargetSeriesPath = seriesDir,
                DedicatedStoragePath = dedicatedDir,
                EnableSymlink = true
            };

            var service = new RezeroSyncService(
                mockApi.Object,
                Mock.Of<IRezeroDownloadService>(),
                NullLogger<RezeroSyncService>.Instance,
                null,
                null,
                CreateMockResolver(),
                config);

            var count = await service.SyncEpisodesAsync(ct);
            count.Should().Be(1);

            File.Exists(ovaFile).Should().BeTrue();
            File.Exists(downloadedMp4).Should().BeTrue();
            (await File.ReadAllTextAsync(ovaFile, ct)).Should().Be("ova-bytes");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task SyncEpisodesAsync_StreamMode_AllCombinationsStandaloneAndMultiVersion_ShouldGenerateAllVariantsAndTvShowNfo()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "RezeroTest_" + Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(tempRoot, "anime-lib");
        Directory.CreateDirectory(libraryRoot);

        try
        {
            var mockApi = CreateMockApi();
            var config = new PluginConfiguration
            {
                IntegrationMode = "StandaloneSeries",
                StandaloneLibraryPath = libraryRoot,
                StandaloneSeriesTitle = "ReZero Perfect Run",
                EnableSymlink = false,
                PreferredLanguage = "both",
                PreferredCaptions = "both",
                EnableMultiVersionFiles = true,
                StreamFormat = "hls_m3u8"
            };

            var service = new RezeroSyncService(
                mockApi.Object,
                Mock.Of<IRezeroDownloadService>(),
                NullLogger<RezeroSyncService>.Instance,
                null,
                null,
                CreateMockResolver(),
                config);

            var count = await service.SyncEpisodesAsync(ct);
            count.Should().Be(12); // 2 languages * 2 caption modes * 3 qualities

            var seriesDir = Path.Combine(libraryRoot, "ReZero Perfect Run");
            File.Exists(Path.Combine(seriesDir, "tvshow.nfo")).Should().BeTrue();

            var strmFiles = Directory.GetFiles(Path.Combine(seriesDir, "Season 00"), "*.strm");
            strmFiles.Should().HaveCount(12);
            (await File.ReadAllTextAsync(strmFiles[0], ct)).Should().Contain("/HLS/");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task SyncEpisodesAsync_DownloadMode_ShouldRespectMaxDownloadsCapWriteNfoAndCleanOppositeStrm()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "RezeroDlSyncTest_" + Guid.NewGuid().ToString("N"));
        var seriesDir = Path.Combine(tempRoot, "tv", "Re - ZERO");
        var seasonDir = Path.Combine(seriesDir, "Season 00");
        Directory.CreateDirectory(seasonDir);

        var staleStrm = Path.Combine(seasonDir, "Re ZERO Starting Life in Another World - S00E01 - [Arc 01] Arrival.strm");
        await File.WriteAllTextAsync(staleStrm, "https://example.com/old.mp4", ct);

        try
        {
            var mockApi = CreateMockApi(twoEpisodes: true);
            var mockDl = new Mock<IRezeroDownloadService>();
            mockDl.Setup(d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
                .Returns<string, string, bool, CancellationToken>(async (_, dest, _, token) =>
                {
                    await File.WriteAllTextAsync(dest, "downloaded-mp4-data", token);
                    return new EpisodeDownloadResult(true, false, DownloadOutcome.Downloaded, 19);
                });

            var config = new PluginConfiguration
            {
                SyncMode = "Download",
                MaxDownloadsPerRun = 1,
                SkipExistingDownloads = true,
                CleanOppositeModeFiles = true,
                TargetSeriesPath = seriesDir,
                DedicatedStoragePath = string.Empty,
                EnableSymlink = true
            };

            var service = new RezeroSyncService(
                mockApi.Object,
                mockDl.Object,
                NullLogger<RezeroSyncService>.Instance,
                null,
                null,
                CreateMockResolver(),
                config);

            var count = await service.SyncEpisodesAsync(ct);
            count.Should().Be(1);

            var mp4Files = Directory.GetFiles(seasonDir, "*.mp4");
            var nfoFiles = Directory.GetFiles(seasonDir, "*.nfo");
            mp4Files.Should().HaveCount(1);
            nfoFiles.Should().HaveCount(1);
            File.Exists(staleStrm).Should().BeFalse();

            // Verify EP 2 was not requested because MaxDownloadsPerRun = 1 was hit
            mockDl.Verify(d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task SyncEpisodesAsync_DownloadMode_ShouldSkipExistingVerifiedAndContinueIncrementalDownload()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "RezeroDlSyncTest_" + Guid.NewGuid().ToString("N"));
        var seriesDir = Path.Combine(tempRoot, "tv", "Re - ZERO");
        var seasonDir = Path.Combine(seriesDir, "Season 00");
        Directory.CreateDirectory(seasonDir);

        try
        {
            var mockApi = CreateMockApi(twoEpisodes: true);
            var mockDl = new Mock<IRezeroDownloadService>();
            var callIndex = 0;
            mockDl.Setup(d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
                .Returns<string, string, bool, CancellationToken>(async (_, dest, _, token) =>
                {
                    callIndex++;
                    if (callIndex == 1)
                    {
                        await File.WriteAllTextAsync(dest, "existing-v1", token);
                        return new EpisodeDownloadResult(true, true, DownloadOutcome.SkippedExisting, 0);
                    }

                    await File.WriteAllTextAsync(dest, "new-ep2", token);
                    return new EpisodeDownloadResult(true, false, DownloadOutcome.Downloaded, 7);
                });

            var config = new PluginConfiguration
            {
                SyncMode = "Download",
                MaxDownloadsPerRun = 1,
                SkipExistingDownloads = true,
                CleanOppositeModeFiles = true,
                TargetSeriesPath = seriesDir
            };

            var service = new RezeroSyncService(
                mockApi.Object,
                mockDl.Object,
                NullLogger<RezeroSyncService>.Instance,
                null,
                null,
                CreateMockResolver(),
                config);

            var count = await service.SyncEpisodesAsync(ct);
            count.Should().Be(2); // 1 skipped existing + 1 new download up to cap=1
            mockDl.Verify(d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }
}
