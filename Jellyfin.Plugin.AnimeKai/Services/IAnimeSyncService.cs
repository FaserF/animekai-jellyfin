using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Providers;

namespace Jellyfin.Plugin.AnimeKai.Services;

/// <summary>
/// Overall result of a multi-provider synchronization run.
/// </summary>
public record OverallSyncResult(
    bool Success,
    int TotalEpisodesSynced,
    List<ProviderSyncResult> ProviderResults,
    string Message);

/// <summary>
/// Service coordinating synchronization across all active anime fan-edits and streaming providers.
/// </summary>
public interface IAnimeSyncService
{
    /// <summary>
    /// Executes synchronization for all enabled providers, or an optional single provider by ID.
    /// </summary>
    /// <param name="specificProviderId">Optional provider filter (e.g. "onepace" or null for all).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <returns>The aggregated sync result.</returns>
    Task<OverallSyncResult> SyncAllAsync(string? specificProviderId = null, CancellationToken cancellationToken = default, IProgress<double>? progress = null);

    /// <summary>
    /// Resolves the dedicated local storage root directory (e.g. /data/animekai on SSD or fallback).
    /// </summary>
    /// <returns>The storage root directory path.</returns>
    string ResolveDedicatedStorageRoot();
}
