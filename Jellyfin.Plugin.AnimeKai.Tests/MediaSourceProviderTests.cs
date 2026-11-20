using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.AnimeKai.Api;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Providers;
using Jellyfin.Plugin.AnimeKai.Providers.Rezero;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AnimeKai.Tests;

public class MediaSourceProviderTests
{
    private static RezeroStreamProvider CreateRezeroProvider()
    {
        var mockApi = new Mock<IRezeroApiClient>();
        var mockDl = new Mock<IRezeroDownloadService>();
        var mockLib = new Mock<ILibraryManager>();
        return new RezeroStreamProvider(
            mockApi.Object,
            mockDl.Object,
            mockLib.Object,
            NullLogger<RezeroStreamProvider>.Instance);
    }

    [Fact]
    public async Task GetMediaSources_RezeroEpisode_ShouldReturnMultipleQualities()
    {
        var provider = CreateRezeroProvider();

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO, Starting Life in Another World/Season 00/Re ZERO Starting Life in Another World - S00E01 - [Arc 01] Episode 1.strm"
        };

        var sources = (await provider.GetMediaSourcesAsync(episode, CancellationToken.None)).ToList();

        sources.Should().NotBeEmpty();
        sources.Should().HaveCount(4); // JP CC, JP Clean, EN Clean, EN CC

        sources[0].Name.Should().Contain("Japanese");
        sources[0].SupportsDirectPlay.Should().BeTrue();
        sources[0].Path.Should().Contain("auto");
        sources[0].Path.Should().Contain("JP%20BURNED%20CC");
        sources[0].MediaStreams.Should().Contain(s => s.Type == MediaStreamType.Subtitle && s.Language == "eng");

        sources.Should().Contain(s => s.Name.Contains("Clean") && s.Path.Contains("JP/index.m3u8"));
        sources.Should().Contain(s => s.Name.Contains("English Dub"));
    }

    [Fact]
    public async Task GetMediaSources_TitleContainingEnLetters_ShouldKeepJapaneseDefault()
    {
        var provider = CreateRezeroProvider();

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO/Season 00/Re ZERO Starting Life in Another World - S00E03 - [Arc 02] The Garden.strm"
        };

        var sources = (await provider.GetMediaSourcesAsync(episode, CancellationToken.None)).ToList();

        sources.Should().HaveCount(4);
        sources[0].Path.Should().Contain("JP%20BURNED%20CC");
        sources[0].Path.Should().NotContain("EN");
    }

    [Fact]
    public async Task GetMediaSources_ExplicitEnglishSubtitlesTag_ShouldSelectEnglishStream()
    {
        var provider = CreateRezeroProvider();

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO/Season 00/Re ZERO Starting Life in Another World - S00E03 - [Arc 02] Episode 1 [EN Subtitles].strm"
        };

        var sources = (await provider.GetMediaSourcesAsync(episode, CancellationToken.None)).ToList();

        sources.Should().HaveCount(4);
        sources.Should().Contain(s => s.Path.Contains("EN%20BURNED%20CC"));
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
            var provider = CreateRezeroProvider();
            var episode = new Episode { Path = strmPath };

            var sources = (await provider.GetMediaSourcesAsync(episode, TestContext.Current.CancellationToken)).ToList();

            sources.Should().HaveCount(4);
            sources.Should().Contain(s => s.Path == "https://media.rezeroperfect.run/HLS/ARC%2003/EP%204/auto/ARC%2003%20-%20EP%204%20-%20EN/index.m3u8");
            sources.Should().Contain(s => s.Path == "https://media.rezeroperfect.run/HLS/ARC%2003/EP%204/auto/ARC%2003%20-%20EP%204%20-%20JP/index.m3u8");
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
        var provider = CreateRezeroProvider();

        var episode = new Episode
        {
            Path = "/data/tv/Attack on Titan/Season 01/Attack on Titan - S01E01.mkv"
        };

        var sources = (await provider.GetMediaSourcesAsync(episode, CancellationToken.None)).ToList();

        sources.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMediaSources_LocalMp4Download_ShouldReturnEmptySoNativeFilePlaysDirectly()
    {
        var provider = CreateRezeroProvider();

        var episode = new Episode
        {
            Path = "/data/tv/Re - ZERO, Starting Life in Another World/Season 00/Re ZERO Starting Life in Another World - S00E01 - [Arc 01] Episode 1.mp4"
        };

        var sources = (await provider.GetMediaSourcesAsync(episode, TestContext.Current.CancellationToken)).ToList();

        sources.Should().BeEmpty();
    }
}
