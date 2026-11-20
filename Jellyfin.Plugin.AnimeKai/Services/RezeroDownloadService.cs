using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Services;

/// <summary>
/// Downloads native 1080p MP4 episodes sequentially using HEAD slot probing, atomic .part writes, and size verification.
/// </summary>
public class RezeroDownloadService : IRezeroDownloadService
{
    private const int BufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly ILogger<RezeroDownloadService> _logger;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxSlotRetries;
    private readonly int _maxDownloadAttempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroDownloadService"/> class.
    /// </summary>
    public RezeroDownloadService(HttpClient httpClient, ILogger<RezeroDownloadService> logger)
        : this(httpClient, logger, TimeSpan.FromSeconds(20), maxSlotRetries: 15, maxDownloadAttempts: 3)
    {
    }

    internal RezeroDownloadService(
        HttpClient httpClient,
        ILogger<RezeroDownloadService> logger,
        TimeSpan retryDelay,
        int maxSlotRetries = 15,
        int maxDownloadAttempts = 3)
    {
        _httpClient = httpClient;
        _logger = logger;
        _retryDelay = retryDelay;
        _maxSlotRetries = Math.Max(1, maxSlotRetries);
        _maxDownloadAttempts = Math.Max(1, maxDownloadAttempts);
    }

    /// <inheritdoc />
    public async Task<EpisodeDownloadResult> DownloadEpisodeAsync(
        string url,
        string destinationPath,
        bool skipExisting = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return new EpisodeDownloadResult(false, false, DownloadOutcome.Failed, 0, "Download URL or destination path is empty.");
        }

        var (slotReady, terminalOutcome, expectedLength, slotError) = await WaitForDownloadSlotAsync(url, cancellationToken).ConfigureAwait(false);
        if (!slotReady)
        {
            return new EpisodeDownloadResult(false, false, terminalOutcome ?? DownloadOutcome.Failed, 0, slotError);
        }

        if (skipExisting && File.Exists(destinationPath))
        {
            var existingLength = new FileInfo(destinationPath).Length;
            var sizeMatches = expectedLength is > 0 ? existingLength == expectedLength.Value : existingLength > 0;
            if (sizeMatches)
            {
                return new EpisodeDownloadResult(true, true, DownloadOutcome.SkippedExisting, 0);
            }
        }

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var partPath = destinationPath + ".part";
        Exception? lastException = null;

        for (var attempt = 1; attempt <= _maxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == _maxDownloadAttempts)
                    {
                        var outcome = MapFailureStatus(response.StatusCode);
                        return new EpisodeDownloadResult(false, false, outcome, 0, $"HTTP {(int)response.StatusCode}");
                    }

                    await DelayBeforeRetryAsync(null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var targetLength = expectedLength is > 0 ? expectedLength : response.Content.Headers.ContentLength;
                await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    await remoteStream.CopyToAsync(fileStream, BufferSize, cancellationToken).ConfigureAwait(false);
                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var actualBytes = new FileInfo(partPath).Length;
                if (targetLength is > 0 && actualBytes != targetLength.Value)
                {
                    TryDeleteFile(partPath);
                    lastException = new IOException($"Incomplete download: expected {targetLength.Value} bytes, got {actualBytes} bytes.");
                    _logger.LogDebug("Truncated payload for {Url} on attempt {Attempt}: {Actual}/{Expected}", url, attempt, actualBytes, targetLength.Value);
                    if (attempt < _maxDownloadAttempts)
                    {
                        await DelayBeforeRetryAsync(null, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return new EpisodeDownloadResult(false, false, DownloadOutcome.Failed, 0, lastException.Message);
                }

                File.Move(partPath, destinationPath, overwrite: true);
                return new EpisodeDownloadResult(true, false, DownloadOutcome.Downloaded, actualBytes);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(partPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteFile(partPath);
                lastException = ex;
                _logger.LogDebug(ex, "Download attempt {Attempt}/{Max} failed for {Url}", attempt, _maxDownloadAttempts, url);
                if (attempt < _maxDownloadAttempts)
                {
                    await DelayBeforeRetryAsync(null, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new EpisodeDownloadResult(false, false, DownloadOutcome.Failed, 0, lastException?.Message ?? "Download failed.");
    }

    private async Task<(bool Ready, DownloadOutcome? Outcome, long? ContentLength, string? Error)> WaitForDownloadSlotAsync(
        string url,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maxSlotRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                headReq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

                using var response = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status is 200 or 206)
                {
                    return (true, null, response.Content.Headers.ContentLength, null);
                }

                if (status == 403)
                {
                    return (false, DownloadOutcome.Blocked, null, "Access blocked by upstream (HTTP 403).");
                }

                if (status is 404 or 410)
                {
                    return (false, DownloadOutcome.Missing, null, $"Episode file not found on upstream (HTTP {status}).");
                }

                if (status is 429 or 503)
                {
                    if (attempt == _maxSlotRetries)
                    {
                        return (false, DownloadOutcome.BusyTimeout, null, $"Upstream download slot remained busy after {_maxSlotRetries} attempts (HTTP {status}).");
                    }

                    await DelayBeforeRetryAsync(response.Headers.RetryAfter?.Delta, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (attempt == _maxSlotRetries)
                {
                    return (false, DownloadOutcome.Failed, null, $"Unexpected HEAD status {status}.");
                }

                await DelayBeforeRetryAsync(null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == _maxSlotRetries)
                {
                    return (false, DownloadOutcome.Failed, null, ex.Message);
                }

                await DelayBeforeRetryAsync(null, cancellationToken).ConfigureAwait(false);
            }
        }

        return (false, DownloadOutcome.BusyTimeout, null, "Download slot check timed out.");
    }

    private Task DelayBeforeRetryAsync(TimeSpan? serverRetryAfter, CancellationToken cancellationToken)
    {
        if (_retryDelay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var delay = serverRetryAfter is { } d && d > TimeSpan.Zero && d <= TimeSpan.FromMinutes(2) ? d : _retryDelay;
        return Task.Delay(delay, cancellationToken);
    }

    private static DownloadOutcome MapFailureStatus(HttpStatusCode statusCode) => (int)statusCode switch
    {
        403 => DownloadOutcome.Blocked,
        404 or 410 => DownloadOutcome.Missing,
        429 or 503 => DownloadOutcome.BusyTimeout,
        _ => DownloadOutcome.Failed
    };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Ignore cleanup failure for transient .part files
        }
    }
}
