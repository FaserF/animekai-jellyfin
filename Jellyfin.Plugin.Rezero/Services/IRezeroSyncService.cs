using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Rezero.Services;

/// <summary>
/// Service managing .strm and .nfo streaming synchronization for Re:Zero Perfect Run.
/// </summary>
public interface IRezeroSyncService
{
    /// <summary>
    /// Executes episode synchronization, writing streaming pointers and metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <returns>Total number of episodes synchronized.</returns>
    Task<int> SyncEpisodesAsync(CancellationToken cancellationToken = default, IProgress<double>? progress = null);

    /// <summary>
    /// Resolves the file system path for the target Re:Zero series directory.
    /// </summary>
    string? ResolveSeriesPath();
}
