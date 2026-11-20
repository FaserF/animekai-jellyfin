using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.Rezero.Models;
using Jellyfin.Plugin.Rezero.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Rezero.Tests;

public class DownloadServiceTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    [Fact]
    public async Task DownloadEpisodeAsync_Head200AndGet_ShouldStreamPartAndAtomicallyMoveToMp4()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroDlTest_" + Guid.NewGuid().ToString("N"));
        var destPath = Path.Combine(tempDir, "ep01.mp4");
        var payload = Encoding.UTF8.GetBytes("FAKE_MP4_BINARY_PAYLOAD_BYTES_123456");

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = payload.Length;
                return head;
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            get.Content.Headers.ContentLength = payload.Length;
            return get;
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var service = new RezeroDownloadService(httpClient, NullLogger<RezeroDownloadService>.Instance, TimeSpan.Zero);

            var result = await service.DownloadEpisodeAsync("https://media.rezeroperfect.run/downloads/ep1.mp4", destPath, true, ct);

            result.Succeeded.Should().BeTrue();
            result.Skipped.Should().BeFalse();
            result.Outcome.Should().Be(DownloadOutcome.Downloaded);
            result.BytesDownloaded.Should().Be(payload.Length);
            File.Exists(destPath).Should().BeTrue();
            File.Exists(destPath + ".part").Should().BeFalse();
            (await File.ReadAllBytesAsync(destPath, ct)).Should().Equal(payload);
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
    public async Task DownloadEpisodeAsync_Busy503And429_ShouldRetryAndSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroDlTest_" + Guid.NewGuid().ToString("N"));
        var destPath = Path.Combine(tempDir, "ep02.mp4");
        var payload = Encoding.UTF8.GetBytes("RETRY_SUCCEEDED_MP4");
        var headCalls = 0;

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                headCalls++;
                if (headCalls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                }

                if (headCalls == 2)
                {
                    var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                    return busy;
                }

                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                ok.Content.Headers.ContentLength = payload.Length;
                return ok;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var service = new RezeroDownloadService(httpClient, NullLogger<RezeroDownloadService>.Instance, TimeSpan.Zero, maxSlotRetries: 5);

            var result = await service.DownloadEpisodeAsync("https://media.rezeroperfect.run/downloads/ep2.mp4", destPath, true, ct);

            headCalls.Should().Be(3);
            result.Succeeded.Should().BeTrue();
            result.Outcome.Should().Be(DownloadOutcome.Downloaded);
            File.Exists(destPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, DownloadOutcome.Blocked)]
    [InlineData(HttpStatusCode.NotFound, DownloadOutcome.Missing)]
    public async Task DownloadEpisodeAsync_TerminalHttpStatus_ShouldFailWithoutCreatingFiles(HttpStatusCode status, DownloadOutcome expectedOutcome)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroDlTest_" + Guid.NewGuid().ToString("N"));
        var destPath = Path.Combine(tempDir, "ep.mp4");

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));

        try
        {
            using var httpClient = new HttpClient(handler);
            var service = new RezeroDownloadService(httpClient, NullLogger<RezeroDownloadService>.Instance, TimeSpan.Zero, maxSlotRetries: 3);

            var result = await service.DownloadEpisodeAsync("https://media.rezeroperfect.run/downloads/ep.mp4", destPath, true, ct);

            result.Succeeded.Should().BeFalse();
            result.Outcome.Should().Be(expectedOutcome);
            File.Exists(destPath).Should().BeFalse();
            File.Exists(destPath + ".part").Should().BeFalse();
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
    public async Task DownloadEpisodeAsync_SkipWhenSizeMatches_AndRedownloadWhenReEdited()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroDlTest_" + Guid.NewGuid().ToString("N"));
        var destPath = Path.Combine(tempDir, "ep03.mp4");
        Directory.CreateDirectory(tempDir);

        var v1 = Encoding.UTF8.GetBytes("ORIGINAL_EPISODE_V1");
        var v2 = Encoding.UTF8.GetBytes("RE_EDITED_EPISODE_VERSION_2_LONGER");
        await File.WriteAllBytesAsync(destPath, v1, ct);

        long currentRemoteLength = v1.Length;
        byte[] currentRemoteBytes = v1;
        var getCalls = 0;

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = currentRemoteLength;
                return head;
            }

            getCalls++;
            var get = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(currentRemoteBytes) };
            get.Content.Headers.ContentLength = currentRemoteLength;
            return get;
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var service = new RezeroDownloadService(httpClient, NullLogger<RezeroDownloadService>.Instance, TimeSpan.Zero);

            var firstRun = await service.DownloadEpisodeAsync("https://media.rezeroperfect.run/downloads/ep3.mp4", destPath, skipExisting: true, ct);
            firstRun.Succeeded.Should().BeTrue();
            firstRun.Skipped.Should().BeTrue();
            firstRun.Outcome.Should().Be(DownloadOutcome.SkippedExisting);
            getCalls.Should().Be(0);

            currentRemoteLength = v2.Length;
            currentRemoteBytes = v2;
            var secondRun = await service.DownloadEpisodeAsync("https://media.rezeroperfect.run/downloads/ep3.mp4", destPath, skipExisting: true, ct);
            secondRun.Succeeded.Should().BeTrue();
            secondRun.Skipped.Should().BeFalse();
            secondRun.Outcome.Should().Be(DownloadOutcome.Downloaded);
            getCalls.Should().Be(1);
            (await File.ReadAllBytesAsync(destPath, ct)).Should().Equal(v2);
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
    public async Task DownloadEpisodeAsync_TruncatedPayload_ShouldCleanUpPartAndFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "RezeroDlTest_" + Guid.NewGuid().ToString("N"));
        var destPath = Path.Combine(tempDir, "ep04.mp4");
        var truncated = Encoding.UTF8.GetBytes("SHORT");

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = 9999;
                return head;
            }

            // Simulate server closing stream after 5 bytes while declaring Content-Length 9999
            var partialContent = new ByteArrayContent(truncated);
            partialContent.Headers.ContentLength = 9999;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = partialContent };
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var service = new RezeroDownloadService(httpClient, NullLogger<RezeroDownloadService>.Instance, TimeSpan.Zero, maxSlotRetries: 2, maxDownloadAttempts: 2);

            var result = await service.DownloadEpisodeAsync("https://media.rezeroperfect.run/downloads/ep4.mp4", destPath, true, ct);

            result.Succeeded.Should().BeFalse();
            result.Outcome.Should().Be(DownloadOutcome.Failed);
            File.Exists(destPath).Should().BeFalse();
            File.Exists(destPath + ".part").Should().BeFalse();
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
