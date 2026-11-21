using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Providers;
using Jellyfin.Plugin.AnimeKai.Providers.BleachKai;
using Jellyfin.Plugin.AnimeKai.Providers.Consumet;
using Jellyfin.Plugin.AnimeKai.Providers.NarutoKai;
using Jellyfin.Plugin.AnimeKai.Providers.OnePace;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AnimeKai.Tests;

public class ProviderTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    [Fact]
    public void NarutoKaiStreamProvider_ShouldContain72CanonVolumes()
    {
        var volumes = NarutoKaiStreamProvider.GetVolumes();
        volumes.Should().HaveCount(72);
        volumes.First().VolumeNumber.Should().Be(1);
        volumes.Last().VolumeNumber.Should().Be(72);
        volumes.First().Chapters.Should().Contain("1-4");
    }

    [Fact]
    public void BleachKaiStreamProvider_ShouldContain36CanonVolumes()
    {
        var volumes = BleachKaiStreamProvider.GetVolumes();
        volumes.Should().HaveCount(36);
        volumes.First().VolumeNumber.Should().Be(1);
        volumes.Last().VolumeNumber.Should().Be(36);
        volumes.First().Chapters.Should().Contain("1-25");
    }

    [Fact]
    public async Task NarutoKaiStreamProvider_SyncEpisodesAsync_ShouldWriteStrmAndNfoFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "NarutoKaiTest_" + Guid.NewGuid().ToString("N"));
        var showDir = Path.Combine(tempRoot, "Naruto Kai");
        Directory.CreateDirectory(showDir);

        try
        {
            var config = new PluginConfiguration
            {
                EnableNarutoKai = true,
                NarutoKaiTargetLibraryPath = showDir
            };

            var provider = new NarutoKaiStreamProvider(new HttpClient(), Mock.Of<ILibraryManager>(), NullLogger<NarutoKaiStreamProvider>.Instance);
            var context = new SyncContext(config, tempRoot, false);

            var result = await provider.SyncEpisodesAsync(context, null, ct);
            result.Success.Should().BeTrue();
            result.EpisodesSynced.Should().Be(72);

            File.Exists(Path.Combine(showDir, "tvshow.nfo")).Should().BeTrue();
            var strmFiles = Directory.GetFiles(showDir, "*.strm", SearchOption.AllDirectories);
            strmFiles.Should().HaveCount(72);

            var sampleStrm = await File.ReadAllTextAsync(strmFiles[0], ct);
            sampleStrm.Should().StartWith("http://consumet-proxy:3000/anime/narutokai/stream/");
            sampleStrm.Should().EndWith("/master.m3u8");

            var rawBytes = await File.ReadAllBytesAsync(strmFiles[0], ct);
            var hasBom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
            hasBom.Should().BeFalse("Generated .strm files must not contain UTF-8 BOM");
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
    public async Task BleachKaiStreamProvider_SyncEpisodesAsync_ShouldWriteStrmAndNfoFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "BleachKaiTest_" + Guid.NewGuid().ToString("N"));
        var showDir = Path.Combine(tempRoot, "Bleach Kai");
        Directory.CreateDirectory(showDir);

        try
        {
            var config = new PluginConfiguration
            {
                EnableBleachKai = true,
                BleachKaiTargetLibraryPath = showDir
            };

            var provider = new BleachKaiStreamProvider(new HttpClient(), Mock.Of<ILibraryManager>(), NullLogger<BleachKaiStreamProvider>.Instance);
            var context = new SyncContext(config, tempRoot, false);

            var result = await provider.SyncEpisodesAsync(context, null, ct);
            result.Success.Should().BeTrue();
            result.EpisodesSynced.Should().Be(36);

            File.Exists(Path.Combine(showDir, "tvshow.nfo")).Should().BeTrue();
            var strmFiles = Directory.GetFiles(showDir, "*.strm", SearchOption.AllDirectories);
            strmFiles.Should().HaveCount(36);

            var sampleStrm = await File.ReadAllTextAsync(strmFiles[0], ct);
            sampleStrm.Should().StartWith("http://consumet-proxy:3000/anime/bleachkai/stream/");
            sampleStrm.Should().EndWith("/master.m3u8");

            var rawBytes = await File.ReadAllBytesAsync(strmFiles[0], ct);
            var hasBom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
            hasBom.Should().BeFalse("Generated .strm files must not contain UTF-8 BOM");
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
    public async Task OnePaceStreamProvider_FallbackCatalog_ShouldSyncIndependentShowSeasons()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "OnePaceTest_" + Guid.NewGuid().ToString("N"));
        var showDir = Path.Combine(tempRoot, "One Pace");
        Directory.CreateDirectory(showDir);

        // Simulating 404 or network timeout on onepace.net so fallback catalog activates
        var handler = new DelegatingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler);

        try
        {
            var config = new PluginConfiguration
            {
                EnableOnePace = true,
                OnePaceTargetLibraryPath = showDir,
                OnePaceIntegrationMode = "StandaloneSeries",
                OnePacePreferredQuality = 1080,
                OnePaceLanguage = "sub"
            };

            var provider = new OnePaceStreamProvider(httpClient, Mock.Of<ILibraryManager>(), NullLogger<OnePaceStreamProvider>.Instance);
            var context = new SyncContext(config, tempRoot, false);

            var result = await provider.SyncEpisodesAsync(context, null, ct);
            result.Success.Should().BeTrue();
            result.EpisodesSynced.Should().BeGreaterThan(0);

            File.Exists(Path.Combine(showDir, "tvshow.nfo")).Should().BeTrue();
            var seasonDirs = Directory.GetDirectories(showDir, "Season *");
            seasonDirs.Should().NotBeEmpty();

            var strmFiles = Directory.GetFiles(showDir, "*.strm", SearchOption.AllDirectories);
            strmFiles.Should().NotBeEmpty();
            var sampleContent = await File.ReadAllTextAsync(strmFiles[0], ct);
            sampleContent.Should().Contain("pixeldrain.net/api/file/");
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
    public void OnePaceStreamProvider_ParseWatchPage_ShouldExtractArcsAndEpisodesFromNextJsHtml()
    {
        var sampleHtml = """
        <li data-timeline-segment="romance-dawn">
            <h2>Romance Dawn</h2>
            <p>Luffy sets out on his journey to become King of the Pirates.</p>
            <h3>01: ROMANCE DAWN, ~The Dawn of an Adventure~</h3>
            <span>English Subtitles</span>
            <a href="https://pixeldrain.net/u/RwHyfKZs">Pixeldrain: 480p</a>
            <a href="https://pixeldrain.net/u/cbjgKSRu">Pixeldrain: 1080p</a>
            <span>English Dub</span>
            <a href="https://pixeldrain.net/u/gpR8Tf9E">Pixeldrain: 1080p</a>
            <h3>02: They Call Him "Straw Hat" Luffy</h3>
            <span>English Subtitles</span>
            <a href="https://pixeldrain.net/u/wxsv8FWy">Pixeldrain: 1080p</a>
        </li>
        """;

        var catalog = OnePaceStreamProvider.ParseWatchPage(sampleHtml, new Uri("https://onepace.net/en/watch"));
        catalog.Should().HaveCount(1);

        var arc = catalog[0];
        arc.Slug.Should().Be("romance-dawn");
        arc.Title.Should().Be("Romance Dawn");
        arc.Episodes.Should().HaveCount(2);

        var ep1 = arc.Episodes[0];
        ep1.Number.Should().Be(1);
        ep1.Title.Should().Be("ROMANCE DAWN, ~The Dawn of an Adventure~");
        ep1.Sources.Should().HaveCount(3);
        ep1.Sources.Any(s => s.Language == OnePaceLanguage.Sub && s.Quality == 1080 && s.FileId == "cbjgKSRu").Should().BeTrue();
        ep1.Sources.Any(s => s.Language == OnePaceLanguage.Dub && s.Quality == 1080 && s.FileId == "gpR8Tf9E").Should().BeTrue();
    }

    [Fact]
    public void AnimeStorageLinker_NfoShouldIncludeLockDataAndSuppressInvalidImages()
    {
        var showNfo = Jellyfin.Plugin.AnimeKai.Services.AnimeStorageLinker.BuildTvShowNfo(
            "Bleach Kai",
            "Manga canon edit.",
            posterUrl: "https://archive.org/download/bleach-kai-posters/bleach_kai_poster.jpg");

        showNfo.Should().Contain("<lockdata>true</lockdata>");
        showNfo.Should().NotContain("archive.org/download");
        showNfo.Should().NotContain("<thumb");

        var epNfo = Jellyfin.Plugin.AnimeKai.Services.AnimeStorageLinker.BuildEpisodeNfo(
            "Vol 01",
            "Bleach Kai",
            1,
            1,
            "Episode plot.",
            thumbUrl: "https://archive.org/download/bleach-kai-covers/vol_01.jpg");

        epNfo.Should().Contain("<lockdata>true</lockdata>");
        epNfo.Should().NotContain("archive.org/download");
        epNfo.Should().NotContain("<thumb");
    }

    [Fact]
    public async Task ConsumetStreamProvider_MockServer_ShouldSyncAnimeEpisodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "ConsumetTest_" + Guid.NewGuid().ToString("N"));
        var showDir = Path.Combine(tempRoot, "Anime");
        Directory.CreateDirectory(showDir);

        var infoJson = """
        {
            "id": "steinsgate",
            "title": "Steins;Gate",
            "description": "Okabe Rintaro invents a phone microwave.",
            "totalEpisodes": 1,
            "episodes": [
                {
                    "id": "steinsgate-episode-1",
                    "number": 1,
                    "title": "Turning Point"
                }
            ]
        }
        """;

        var watchJson = """
        {
            "sources": [
                {
                    "url": "https://stream.example.com/steinsgate/ep1.m3u8",
                    "quality": "default",
                    "isM3U8": true
                }
            ]
        }
        """;

        var handler = new DelegatingHandlerStub(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/info"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(infoJson, Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri.AbsolutePath.Contains("/watch"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(watchJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);

        try
        {
            var config = new PluginConfiguration
            {
                EnableConsumet = true,
                ConsumetApiUrl = "http://127.0.0.1:3000",
                ConsumetProvider = "gogoanime",
                ConsumetSubscribedShows = "steinsgate",
                ConsumetTargetLibraryPath = showDir
            };

            var provider = new ConsumetStreamProvider(httpClient, Mock.Of<ILibraryManager>(), NullLogger<ConsumetStreamProvider>.Instance);
            var context = new SyncContext(config, tempRoot, false);

            var result = await provider.SyncEpisodesAsync(context, null, ct);
            result.Success.Should().BeTrue();
            result.EpisodesSynced.Should().Be(1);

            var targetFolder = Directory.GetDirectories(showDir).FirstOrDefault();
            targetFolder.Should().NotBeNull();
            File.Exists(Path.Combine(targetFolder!, "tvshow.nfo")).Should().BeTrue();

            var strmFile = Directory.GetFiles(targetFolder!, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
            strmFile.Should().NotBeNull();
            var strmContent = await File.ReadAllTextAsync(strmFile!, ct);
            strmContent.Should().Be("https://stream.example.com/steinsgate/ep1.m3u8");
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
