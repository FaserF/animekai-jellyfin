using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private static Mock<IRezeroApiClient> CreateMockApi()
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

        mockApi.Setup(a => a.GetArcEpisodesAsync("arc01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeDataResponse
            {
                Episodes = new List<EpisodeDto>
                {
                    new()
                    {
                        Number = 1,
                        Title = "Arrival",
                        Sources = new()
                        {
                            ["jp"] = new()
                            {
                                ["cc"] = new()
                                {
                                    ["1080p"] = "ARC%2001/EP%201/1080p/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC/index.m3u8"
                                }
                            }
                        },
                        Segments = new EpisodeSegments
                        {
                            Intro = new SegmentRange { Start = 60, End = 150 },
                            Outro = new SegmentRange { Start = 1200, End = 1290 }
                        }
                    }
                }
            });

        mockApi.Setup(a => a.GetUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdatesResponse());

        mockApi.Setup(a => a.ResolveStreamUrl(It.IsAny<EpisodeDto>(), It.IsAny<ArcDto>(), "jp", "cc", It.IsAny<string>(), It.IsAny<string>()))
            .Returns("https://media.rezeroperfect.run/downloads/ARC%2001/EP%201/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC.mp4");

        return mockApi;
    }

    [Fact]
    public async Task SyncEpisodesAsync_ShouldWriteExpectedStrmAndNfoFiles()
    {
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

            var resolver = new StoragePathResolver(
                getMounts: () => Array.Empty<MountEntry>(),
                isDirectoryWritable: _ => true,
                isNetworkPath: _ => false);

            var service = new RezeroSyncService(
                mockApi.Object,
                NullLogger<RezeroSyncService>.Instance,
                libraryManager: null,
                applicationPaths: null,
                storagePathResolver: resolver,
                configOverride: config);

            var ct = TestContext.Current.CancellationToken;
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
    public async Task SyncEpisodesAsync_ShouldPreserveExistingNonRezeroUserFilesInSeason00()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "RezeroTest_" + Guid.NewGuid().ToString("N"));
        var seriesDir = Path.Combine(tempRoot, "tv", "Re - ZERO");
        var seasonDir = Path.Combine(seriesDir, "Season 00");
        var dedicatedDir = Path.Combine(tempRoot, "rezero", "Season 00");
        Directory.CreateDirectory(seasonDir);
        var ovaFile = Path.Combine(seasonDir, "Memory Snow.mkv");
        await File.WriteAllTextAsync(ovaFile, "ova-bytes", ct);

        try
        {
            var mockApi = CreateMockApi();
            var config = new PluginConfiguration
            {
                TargetSeriesPath = seriesDir,
                DedicatedStoragePath = dedicatedDir,
                EnableSymlink = true
            };

            var resolver = new StoragePathResolver(
                getMounts: () => Array.Empty<MountEntry>(),
                isDirectoryWritable: _ => true,
                isNetworkPath: _ => false);

            var service = new RezeroSyncService(
                mockApi.Object,
                NullLogger<RezeroSyncService>.Instance,
                libraryManager: null,
                applicationPaths: null,
                storagePathResolver: resolver,
                configOverride: config);

            var count = await service.SyncEpisodesAsync(ct);
            count.Should().Be(1);

            File.Exists(ovaFile).Should().BeTrue();
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
}
