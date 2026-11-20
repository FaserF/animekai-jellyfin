using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
        sources[0].Path.Should().Contain("JP%20BURNED%20CC");

        sources[1].Name.Should().Contain("720p");
        sources[1].Path.Should().Contain("720p");

        sources[2].Name.Should().Contain("480p");
        sources[2].Path.Should().Contain("480p");
    }

    [Fact]
    public async Task GetMediaSources_TitleContainingEnLetters_ShouldKeepJapaneseDefault()
    {
        var provider = new RezeroMediaSourceProvider(NullLogger<RezeroMediaSourceProvider>.Instance);

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO/Season 00/Re ZERO Starting Life in Another World - S00E03 - [Arc 02] The Garden.strm"
        };

        var sources = (await provider.GetMediaSources(episode, CancellationToken.None)).ToList();

        sources.Should().HaveCount(3);
        sources[0].Path.Should().Contain("JP%20BURNED%20CC");
        sources[0].Path.Should().NotContain("EN");
    }

    [Fact]
    public async Task GetMediaSources_ExplicitEnglishSubtitlesTag_ShouldSelectEnglishStream()
    {
        var provider = new RezeroMediaSourceProvider(NullLogger<RezeroMediaSourceProvider>.Instance);

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO/Season 00/Re ZERO Starting Life in Another World - S00E03 - [Arc 02] Episode 1 [EN Subtitles].strm"
        };

        var sources = (await provider.GetMediaSources(episode, CancellationToken.None)).ToList();

        sources.Should().HaveCount(3);
        sources[0].Path.Should().Contain("EN%20BURNED%20CC");
    }

    [Fact]
    public async Task GetMediaSources_ExistingStrmFile_ShouldExtractExactUrlPartsFromContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroMediaSourceTest_" + Guid.NewGuid().ToString("N"));
        var strmPath = Path.Combine(tempDir, "Re ZERO - S00E10.strm");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(
            strmPath,
            "https://media.rezeroperfect.run/downloads/ARC%2003/EP%204/ARC%2003%20-%20EP%204%20-%20EN.mp4",
            TestContext.Current.CancellationToken);

        try
        {
            var provider = new RezeroMediaSourceProvider(NullLogger<RezeroMediaSourceProvider>.Instance);
            var episode = new Episode { Path = strmPath };

            var sources = (await provider.GetMediaSources(episode, TestContext.Current.CancellationToken)).ToList();

            sources.Should().HaveCount(3);
            sources[0].Path.Should().Be("https://media.rezeroperfect.run/downloads/ARC%2003/EP%204/ARC%2003%20-%20EP%204%20-%20EN.mp4");
            sources[1].Path.Should().Be("https://media.rezeroperfect.run/HLS/ARC%2003/EP%204/720p/ARC%2003%20-%20EP%204%20-%20EN/index.m3u8");
            sources[2].Path.Should().Be("https://media.rezeroperfect.run/HLS/ARC%2003/EP%204/480p/ARC%2003%20-%20EP%204%20-%20EN/index.m3u8");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
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

    [Fact]
    public async Task GetMediaSources_LocalMp4Download_ShouldReturnEmptySoNativeFilePlaysDirectly()
    {
        var provider = new RezeroMediaSourceProvider(NullLogger<RezeroMediaSourceProvider>.Instance);

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO, Starting Life in Another World/Season 00/Re ZERO Starting Life in Another World - S00E01 - [Arc 01] Episode 1.mp4"
        };

        var sources = (await provider.GetMediaSources(episode, TestContext.Current.CancellationToken)).ToList();

        sources.Should().BeEmpty();
    }
}
