using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Configuration;

namespace Jellyfin.Plugin.Rezero.Services;

/// <summary>
/// Service managing .strm streaming and native MP4 download synchronization for Re:Zero Perfect Run.
/// </summary>
public interface IRezeroSyncService
{
    /// <summary>
    /// Executes episode synchronization using the active plugin configuration.
    /// </summary>
    Task<int> SyncEpisodesAsync(CancellationToken cancellationToken = default, IProgress<double>? progress = null);

    /// <summary>
    /// Executes episode synchronization using an optional in-memory configuration override (without mutating persisted settings).
    /// </summary>
    Task<int> SyncEpisodesAsync(PluginConfiguration? runtimeConfigOverride, CancellationToken cancellationToken = default, IProgress<double>? progress = null);

    /// <summary>
    /// Resolves the file system path for the target Re:Zero series directory.
    /// </summary>
    string? ResolveSeriesPath();

    /// <summary>
    /// Resolves the physical local storage path and target symlink path for the current environment.
    /// </summary>
    StorageResolutionResult? ResolveStoragePaths();
}
