using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.AnimeKai.Api;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Models;
using Jellyfin.Plugin.AnimeKai.Providers;
using Jellyfin.Plugin.AnimeKai.Providers.Rezero;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AnimeKai.Tests;

public class AnimeSyncServiceTests
{
    private static Mock<IRezeroApiClient> CreateMockApi()
    {
        var mockApi = new Mock<IRezeroApiClient>();
        mockApi.Setup(a => a.GetArcsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideosResponse
            {
                Arcs = new List<ArcDto>
                {
                    new ArcDto
                    {
                        Id = "arc01",
                        Number = 1,
                        Title = "First Day",
                        Description = "The first day in another world.",
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
                        Segments = new EpisodeSegments
                        {
                            Intro = new SegmentRange { Start = 60, End = 150 },
                            Outro = new SegmentRange { Start = 1200, End = 1290 }
                        }
                    }
                }
            });

        mockApi.Setup(a => a.ResolveStreamUrl(It.IsAny<EpisodeDto>(), It.IsAny<ArcDto>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns("https://media.rezeroperfect.run/HLS/ARC%2001/EP%201/auto/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC/index.m3u8");

        return mockApi;
    }

    [Fact]
    public async Task RezeroStreamProvider_SyncEpisodesAsync_ShouldWriteStrmAndNfo()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "AnimeKai_RezeroTest_" + Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(tempRoot, "storage");
        var showDir = Path.Combine(tempRoot, "tv", "Re - ZERO");
        Directory.CreateDirectory(showDir);

        try
        {
            var config = new PluginConfiguration
            {
                EnableRezero = true,
                TargetSeriesPath = showDir,
                SyncMode = "Stream"
            };

            var mockApi = CreateMockApi();
            var mockDl = new Mock<IRezeroDownloadService>();
            var mockLib = new Mock<ILibraryManager>();

            var provider = new RezeroStreamProvider(
                mockApi.Object,
                mockDl.Object,
                mockLib.Object,
                NullLogger<RezeroStreamProvider>.Instance);

            var context = new SyncContext(config, storageRoot, false);
            var result = await provider.SyncEpisodesAsync(context, null, ct);

            result.Success.Should().BeTrue();
            result.EpisodesSynced.Should().Be(1);

            var seasonDir = Path.Combine(showDir, "Season 00");
            Directory.Exists(seasonDir).Should().BeTrue();

            var strmFiles = Directory.GetFiles(seasonDir, "*.strm");
            strmFiles.Should().HaveCount(1);

            var content = await File.ReadAllTextAsync(strmFiles[0], ct);
            content.Should().Contain("/HLS/ARC%2001/EP%201/");

            var epNfoFiles = Directory.GetFiles(seasonDir, "*S00E*.nfo");
            epNfoFiles.Should().HaveCount(1);
            File.Exists(Path.Combine(seasonDir, "season.nfo")).Should().BeTrue();
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
    public async Task AnimeSyncService_SyncAllAsync_DisabledProviders_ShouldReturnZeroEpisodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var mockProvider = new Mock<IAnimeStreamProvider>();
        mockProvider.Setup(p => p.ProviderId).Returns("test");
        mockProvider.Setup(p => p.IsEnabled(It.IsAny<PluginConfiguration>())).Returns(false);

        var service = new AnimeSyncService(
            new[] { mockProvider.Object },
            Mock.Of<ILibraryManager>(),
            NullLogger<AnimeSyncService>.Instance);

        var result = await service.SyncAllAsync(cancellationToken: ct);
        result.Success.Should().BeTrue();
        result.TotalEpisodesSynced.Should().Be(0);
        mockProvider.Verify(p => p.SyncEpisodesAsync(It.IsAny<SyncContext>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
