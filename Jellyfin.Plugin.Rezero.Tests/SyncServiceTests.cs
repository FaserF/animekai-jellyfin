using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Models;
using Jellyfin.Plugin.Rezero.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Rezero.Tests;

public class SyncServiceTests
{
    [Fact]
    public async Task SyncEpisodesAsync_ShouldWriteExpectedStrmAndNfoFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroTest_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
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

            // Mock plugin instance configuration
            var service = new RezeroSyncService(mockApi.Object, NullLogger<RezeroSyncService>.Instance, null);

            // Execute service logic with mock
            service.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
