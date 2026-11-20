using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Providers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Services;

/// <summary>
/// Orchestrates synchronization across all registered anime fan-edits and streaming providers.
/// </summary>
public class AnimeSyncService : IAnimeSyncService
{
    private readonly IEnumerable<IAnimeStreamProvider> _providers;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AnimeSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeSyncService"/> class.
    /// </summary>
    public AnimeSyncService(
        IEnumerable<IAnimeStreamProvider> providers,
        ILibraryManager libraryManager,
        ILogger<AnimeSyncService> logger)
    {
        _providers = providers;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OverallSyncResult> SyncAllAsync(
        string? specificProviderId = null,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storageRoot = ResolveDedicatedStorageRoot();
        var context = new SyncContext(config, storageRoot, config.EnableSymlink);

        var activeProviders = _providers.Where(p =>
            string.IsNullOrWhiteSpace(specificProviderId)
                ? p.IsEnabled(config)
                : string.Equals(p.ProviderId, specificProviderId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (activeProviders.Count == 0)
        {
            _logger.LogInformation("No anime stream providers currently enabled for synchronization.");
            return new OverallSyncResult(true, 0, new List<ProviderSyncResult>(), "No providers currently enabled.");
        }

        var results = new List<ProviderSyncResult>();
        var totalEpisodes = 0;
        var hasFailures = false;

        for (var i = 0; i < activeProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = activeProviders[i];
            try
            {
                _logger.LogInformation("Executing sync for provider: {Name} ({Id})...", provider.DisplayName, provider.ProviderId);
                var res = await provider.SyncEpisodesAsync(context, null, cancellationToken).ConfigureAwait(false);
                results.Add(res);
                if (res.Success)
                {
                    totalEpisodes += res.EpisodesSynced;
                }
                else
                {
                    hasFailures = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Synchronization failed for provider {Name}", provider.DisplayName);
                hasFailures = true;
                results.Add(new ProviderSyncResult(provider.ProviderId, provider.DisplayName, false, 0, ex.Message));
            }

            progress?.Report((double)(i + 1) / activeProviders.Count * 100.0);
        }

        // Update plugin state
        if (Plugin.Instance != null)
        {
            config.LastSyncUtc = DateTime.UtcNow;
            config.TotalSyncedEpisodes = totalEpisodes;
            config.LastSyncStatus = hasFailures
                ? $"Sync finished with partial errors. {totalEpisodes} episodes synced across {activeProviders.Count} providers."
                : $"Successfully synchronized {totalEpisodes} episodes across {activeProviders.Count} providers.";
            Plugin.Instance.SaveConfiguration();
        }

        if (totalEpisodes > 0)
        {
            _logger.LogInformation("Triggering Jellyfin media library scan for synced anime...");
            _libraryManager.QueueLibraryScan();
        }

        return new OverallSyncResult(!hasFailures, totalEpisodes, results, config.LastSyncStatus);
    }

    /// <inheritdoc />
    public string ResolveDedicatedStorageRoot()
    {
        var config = Plugin.Instance?.Configuration;
        if (!string.IsNullOrWhiteSpace(config?.DedicatedStoragePath))
        {
            try
            {
                Directory.CreateDirectory(config.DedicatedStoragePath);
                return config.DedicatedStoragePath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Configured DedicatedStoragePath {Path} could not be created; falling back.", config.DedicatedStoragePath);
            }
        }

        // Fast local NVMe path e.g. /data/animekai
        var candidateRoots = new[]
        {
            "/data/animekai",
            "/data/rezero",
            "/opt/docker/data/animekai",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeKaiStorage")
        };

        foreach (var candidate in candidateRoots)
        {
            try
            {
                var parent = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
            }
            catch
            {
                // Try next
            }
        }

        var fallback = Path.Combine(Path.GetTempPath(), "AnimeKaiStorage");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
