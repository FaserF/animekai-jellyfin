using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Rezero.ScheduledTasks;

/// <summary>
/// Scheduled task to synchronize Re:Zero Perfect Run episodes and check for upstream updates.
/// </summary>
public class RezeroSyncTask : IScheduledTask
{
    private readonly IRezeroSyncService _syncService;
    private readonly ILogger<RezeroSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezeroSyncTask"/> class.
    /// </summary>
    public RezeroSyncTask(
        IRezeroSyncService syncService,
        ILogger<RezeroSyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Re:Zero Perfect Run Episodes (.strm & .nfo)";

    /// <inheritdoc />
    public string Key => "RezeroPerfectRunSyncTask";

    /// <inheritdoc />
    public string Description => "Polls rezeroperfect.run JSON endpoints, detects new releases or re-edited episodes, and synchronizes streaming pointers and NFO metadata into Jellyfin.";

    /// <inheritdoc />
    public string Category => "Live TV & Streaming";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting scheduled Re:Zero Perfect Run synchronization...");
        try
        {
            var count = await _syncService.SyncEpisodesAsync(cancellationToken, progress).ConfigureAwait(false);
            _logger.LogInformation("Re:Zero Perfect Run sync finished. Total episodes synced: {Count}", count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Re:Zero Perfect Run sync was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred during Re:Zero Perfect Run sync.");
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
