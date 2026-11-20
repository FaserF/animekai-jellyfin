using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Configuration;
using Jellyfin.Plugin.Rezero.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.Controllers;

/// <summary>
/// REST API controller for Re:Zero Perfect Run plugin management.
/// </summary>
[ApiController]
[Route("Rezero")]
public class RezeroController : ControllerBase
{
    private readonly IRezeroSyncService _syncService;
    private readonly IRezeroApiClient _apiClient;
    private readonly ILogger<RezeroController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroController"/> class.
    /// </summary>
    public RezeroController(
        IRezeroSyncService syncService,
        IRezeroApiClient apiClient,
        ILogger<RezeroController> logger)
    {
        _syncService = syncService;
        _apiClient = apiClient;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Retrieves current synchronization status and plugin configuration info.
    /// </summary>
    [HttpGet("Status")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var seriesPath = _syncService.ResolveSeriesPath();
        var storage = _syncService.ResolveStoragePaths();
        var isSymlinkActive = false;
        if (!string.IsNullOrWhiteSpace(storage?.TargetSeasonPath))
        {
            try
            {
                isSymlinkActive = !string.IsNullOrEmpty(new DirectoryInfo(storage.TargetSeasonPath).LinkTarget);
            }
            catch
            {
                isSymlinkActive = false;
            }
        }

        return Ok(new
        {
            SeriesPath = seriesPath,
            TargetSeasonPath = storage?.TargetSeasonPath,
            ResolvedStoragePath = storage?.PhysicalStoragePath,
            StorageDetectionMode = storage?.DetectionMode,
            StorageDetectionReason = storage?.Reason,
            UseSymlink = storage?.UseSymlink ?? Config.EnableSymlink,
            IsSymlinkActive = isSymlinkActive,
            DedicatedStoragePath = Config.DedicatedStoragePath,
            EnableSymlink = Config.EnableSymlink,
            SyncMode = Config.SyncMode,
            MaxDownloadsPerRun = Config.MaxDownloadsPerRun,
            SkipExistingDownloads = Config.SkipExistingDownloads,
            CleanOppositeModeFiles = Config.CleanOppositeModeFiles,
            LastSyncUtc = Config.LastSyncUtc,
            LastSyncStatus = Config.LastSyncStatus,
            TotalSyncedEpisodes = Config.TotalSyncedEpisodes,
            LastUpdateGuid = Config.LastUpdateGuid,
            PreferredLanguage = Config.PreferredLanguage,
            PreferredCaptions = Config.PreferredCaptions,
            StreamFormat = Config.StreamFormat
        });
    }

    /// <summary>
    /// Fetches the latest release updates directly from the official upstream API.
    /// </summary>
    [HttpGet("Updates")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetUpdates(CancellationToken cancellationToken)
    {
        var updates = await _apiClient.GetUpdatesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(updates?.Updates ?? new());
    }

    /// <summary>
    /// Fetches native 1080p MP4 download and BitTorrent manifest information from rezeroperfect.run.
    /// </summary>
    [HttpGet("Downloads")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetDownloadsCatalog(CancellationToken cancellationToken)
    {
        var torrents = await _apiClient.GetTorrentManifestAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            SyncMode = Config.SyncMode,
            DownloadsBaseUrl = Config.DownloadsBaseUrl,
            TorrentsBaseUrl = Config.TorrentsBaseUrl,
            Series = torrents?.Series,
            ArcsCount = torrents?.Arcs?.Count ?? 0,
            EpisodesCount = torrents?.Episodes?.Count ?? 0,
            Arcs = torrents?.Arcs
        });
    }

    /// <summary>
    /// Triggers an immediate synchronization run (optionally using transient overrides without mutating saved settings).
    /// </summary>
    [HttpPost("Sync")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<object>> TriggerSync(
        [FromQuery] string? syncMode = null,
        [FromQuery] int? maxDownloads = null,
        [FromQuery] string? overrideSeriesPath = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Manual Re:Zero sync triggered via API (syncMode={Mode}, maxDownloads={Max}, overridePath={Path})", syncMode ?? Config.SyncMode, maxDownloads, overrideSeriesPath);
        try
        {
            PluginConfiguration? overrideConfig = null;
            if (!string.IsNullOrWhiteSpace(syncMode) || maxDownloads.HasValue || !string.IsNullOrWhiteSpace(overrideSeriesPath))
            {
                overrideConfig = Config.Clone();
                if (!string.IsNullOrWhiteSpace(syncMode))
                {
                    overrideConfig.SyncMode = syncMode.Trim();
                }

                if (maxDownloads.HasValue)
                {
                    overrideConfig.MaxDownloadsPerRun = Math.Max(0, maxDownloads.Value);
                }

                if (!string.IsNullOrWhiteSpace(overrideSeriesPath))
                {
                    overrideConfig.TargetSeriesPath = overrideSeriesPath.Trim();
                    overrideConfig.DedicatedStoragePath = string.Empty;
                }
            }

            var effectiveMode = overrideConfig?.SyncMode ?? Config.SyncMode;
            var count = overrideConfig != null
                ? await _syncService.SyncEpisodesAsync(overrideConfig, cancellationToken).ConfigureAwait(false)
                : await _syncService.SyncEpisodesAsync(cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                Success = true,
                SyncMode = effectiveMode,
                Message = $"Synchronized {count} {(string.Equals(effectiveMode, "Download", StringComparison.OrdinalIgnoreCase) ? "downloaded" : "streaming")} episodes successfully.",
                Count = count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Success = false,
                Message = ex.Message
            });
        }
    }
}
