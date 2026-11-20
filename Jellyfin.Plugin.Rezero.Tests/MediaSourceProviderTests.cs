using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.Rezero.Configuration;
using Jellyfin.Plugin.Rezero.Providers;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Rezero.Tests;

public class MediaSourceProviderTests
{
    [Fact]
    public async Task GetMediaSources_RezeroEpisode_ShouldReturnMultipleQualities()
    {
        var provider = new RezeroMediaSourceProvider(NullLogger<RezeroMediaSourceProvider>.Instance);

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO, Starting Life in Another World/Season 00/Re ZERO Starting Life in Another World - S00E01 - [Arc 01] Episode 1.strm"
        };

        var sources = (await provider.GetMediaSources(episode, CancellationToken.None)).ToList();

        sources.Should().NotBeEmpty();
        sources.Should().HaveCount(3); // 1080p, 720p, 480p

        sources[0].Name.Should().Contain("1080p");
        sources[0].SupportsDirectPlay.Should().BeTrue();

        sources[1].Name.Should().Contain("720p");
        sources[1].Path.Should().Contain("720p");

        sources[2].Name.Should().Contain("480p");
        sources[2].Path.Should().Contain("480p");
    }

    [Fact]
    public async Task GetMediaSources_NonRezeroItem_ShouldReturnEmpty()
    {
        var provider = new RezeroMediaSourceProvider(NullLogger<RezeroMediaSourceProvider>.Instance);

        var episode = new Episode
        {
            Path = "/data/tv/Attack on Titan/Season 01/Attack on Titan - S01E01.mkv"
        };

        var sources = (await provider.GetMediaSources(episode, CancellationToken.None)).ToList();

        sources.Should().BeEmpty();
    }
}
