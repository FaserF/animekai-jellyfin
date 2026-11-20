using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Api;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Providers;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Controllers;

/// <summary>
/// REST API controller for Anime Kai &amp; Fan-Edits plugin management.
/// Supports both /AnimeKai/* and /Rezero/* routes for 100% backward compatibility.
/// </summary>
[ApiController]
[Route("AnimeKai")]
[Route("Rezero")]
public class AnimeController : ControllerBase
{
    private readonly IAnimeSyncService _syncService;
    private readonly IEnumerable<IAnimeStreamProvider> _providers;
    private readonly IRezeroApiClient _rezeroApiClient;
    private readonly ILogger<AnimeController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeController"/> class.
    /// </summary>
    public AnimeController(
        IAnimeSyncService syncService,
        IEnumerable<IAnimeStreamProvider> providers,
        IRezeroApiClient rezeroApiClient,
        ILogger<AnimeController> logger)
    {
        _syncService = syncService;
        _providers = providers;
        _rezeroApiClient = rezeroApiClient;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Retrieves current synchronization status, active providers, and storage detection info.
    /// </summary>
    [HttpGet("Status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var storageRoot = _syncService.ResolveDedicatedStorageRoot();
        var isSymlinkActive = false;
        try
        {
            var rezeroTarget = Path.Combine(storageRoot, "ReZero", Config.SeasonFolder);
            if (Directory.Exists(rezeroTarget))
            {
                isSymlinkActive = !string.IsNullOrEmpty(new DirectoryInfo(rezeroTarget).LinkTarget);
            }
        }
        catch
        {
            isSymlinkActive = false;
        }

        var providerStatuses = _providers.Select(p => new
        {
            p.ProviderId,
            p.DisplayName,
            Enabled = p.IsEnabled(Config)
        }).ToList();

        return Ok(new
        {
            StorageRoot = storageRoot,
            DedicatedStoragePath = Config.DedicatedStoragePath,
            EnableSymlink = Config.EnableSymlink,
            IsSymlinkActive = isSymlinkActive,
            EnableAutoSync = Config.EnableAutoSync,
            SyncIntervalHours = Config.SyncIntervalHours,
            LastSyncUtc = Config.LastSyncUtc,
            LastSyncStatus = Config.LastSyncStatus,
            TotalSyncedEpisodes = Config.TotalSyncedEpisodes,
            CleanOppositeModeFiles = Config.CleanOppositeModeFiles,
            OfferAllQualitiesInPlayer = Config.OfferAllQualitiesInPlayer,
            Providers = providerStatuses,

            // Re:Zero fields for backwards compatibility
            EnableRezero = Config.EnableRezero,
            IntegrationMode = Config.IntegrationMode,
            MatchingStrategy = Config.MatchingStrategy,
            TargetSeriesPath = Config.TargetSeriesPath,
            SeasonFolder = Config.SeasonFolder,
            SeasonNumber = Config.SeasonNumber,
            SyncMode = Config.SyncMode,
            PreferredLanguage = Config.PreferredLanguage,
            PreferredCaptions = Config.PreferredCaptions,
            StreamFormat = Config.StreamFormat,

            // One Pace
            EnableOnePace = Config.EnableOnePace,
            OnePaceIntegrationMode = Config.OnePaceIntegrationMode,
            OnePaceLanguage = Config.OnePaceLanguage,
            OnePacePreferredQuality = Config.OnePacePreferredQuality,

            // Naruto Kai & Bleach Kai
            EnableNarutoKai = Config.EnableNarutoKai,
            EnableBleachKai = Config.EnableBleachKai,

            // Consumet
            EnableConsumet = Config.EnableConsumet,
            ConsumetApiUrl = Config.ConsumetApiUrl,
            ConsumetProvider = Config.ConsumetProvider,
            ConsumetSubscribedShows = Config.ConsumetSubscribedShows
        });
    }

    /// <summary>
    /// Fetches all registered providers and their active states.
    /// </summary>
    [HttpGet("Providers")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetProviders()
    {
        return Ok(_providers.Select(p => new
        {
            p.ProviderId,
            p.DisplayName,
            Enabled = p.IsEnabled(Config)
        }));
    }

    /// <summary>
    /// Fetches latest release updates and changelog.
    /// </summary>
    [HttpGet("Updates")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetUpdates(CancellationToken cancellationToken)
    {
        var updates = await _rezeroApiClient.GetUpdatesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(updates?.Updates ?? new());
    }

    /// <summary>
    /// Triggers an immediate synchronization run across all enabled providers, or a specific provider.
    /// </summary>
    [HttpPost("Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<object>> TriggerSync([FromQuery] string? provider = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Manual sync triggered via REST API (Filter: {Provider})...", provider ?? "All");
        try
        {
            var result = await _syncService.SyncAllAsync(provider, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                Success = result.Success,
                Message = result.Message,
                Count = result.TotalEpisodesSynced,
                Providers = result.ProviderResults
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync triggered via REST API failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Success = false,
                Message = $"Sync failed: {ex.Message}",
                Error = ex.ToString()
            });
        }
    }
}
