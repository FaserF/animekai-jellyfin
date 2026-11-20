using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.AnimeKai.Providers;

/// <summary>
/// Result of a synchronization run for a specific provider.
/// </summary>
public record ProviderSyncResult(
    string ProviderId,
    string DisplayName,
    bool Success,
    int EpisodesSynced,
    string Message);

/// <summary>
/// Context passed into a provider synchronization run.
/// </summary>
public record SyncContext(
    PluginConfiguration Configuration,
    string StorageRoot,
    bool UseSymlink);

/// <summary>
/// Common interface for all anime fan-edits, recuts, and streaming providers.
/// </summary>
public interface IAnimeStreamProvider
{
    /// <summary>
    /// Gets the unique identifier for this provider (e.g. "rezero", "onepace", "narutokai", "bleachkai", "consumet").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the human-readable display name of the provider.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Checks whether this provider is enabled in the current configuration.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>True if enabled; otherwise, false.</returns>
    bool IsEnabled(PluginConfiguration config);

    /// <summary>
    /// Synchronizes episode streams, metadata, and artwork for this provider.
    /// </summary>
    /// <param name="context">The synchronization context.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result.</returns>
    Task<ProviderSyncResult> SyncEpisodesAsync(SyncContext context, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves dynamic in-player MediaSources (qualities, clean vs burned CC, Japanese vs English dub).
    /// </summary>
    /// <param name="item">The Jellyfin media item (episode).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The media sources, or empty if item does not belong to this provider.</returns>
    Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken);
}
