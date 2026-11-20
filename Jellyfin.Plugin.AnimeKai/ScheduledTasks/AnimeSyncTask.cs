using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.ScheduledTasks;

/// <summary>
/// Scheduled task to synchronize anime fan-edits (Re:Zero, One Pace, Naruto Kai, Bleach Kai, Consumet).
/// </summary>
public class AnimeSyncTask : IScheduledTask
{
    private readonly IAnimeSyncService _syncService;
    private readonly ILogger<AnimeSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeSyncTask"/> class.
    /// </summary>
    public AnimeSyncTask(IAnimeSyncService syncService, ILogger<AnimeSyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Anime Kai & Fan-Edits (.strm & .nfo)";

    /// <inheritdoc />
    public string Key => "AnimeKaiFanEditsSyncTask";

    /// <inheritdoc />
    public string Description => "Polls anime fan-edit sources and gateways (Re:Zero, One Pace, Naruto Kai, Bleach Kai, Consumet), generating stream pointers and NFO metadata in Jellyfin.";

    /// <inheritdoc />
    public string Category => "Live TV & Streaming";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting scheduled Anime Kai & Fan-Edits synchronization...");
        try
        {
            var result = await _syncService.SyncAllAsync(null, cancellationToken, progress).ConfigureAwait(false);
            _logger.LogInformation("Anime Kai sync completed: {Count} episodes synced across active providers.", result.TotalEpisodesSynced);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Anime Kai sync task was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred during Anime Kai sync task.");
            throw;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalHours = Plugin.Instance?.Configuration.SyncIntervalHours ?? 12;
        if (intervalHours < 1)
        {
            intervalHours = 12;
        }

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
            },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            }
        };
    }
}
