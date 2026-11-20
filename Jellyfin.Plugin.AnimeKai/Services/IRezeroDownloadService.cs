using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Models;

namespace Jellyfin.Plugin.AnimeKai.Services;

/// <summary>
/// Sequential single-connection HTTP downloader for native 1080p MP4 episodes from rezeroperfect.run.
/// </summary>
public interface IRezeroDownloadService
{
    /// <summary>
    /// Checks upstream download availability/slot and downloads the episode to <paramref name="destinationPath"/>.
    /// </summary>
    Task<EpisodeDownloadResult> DownloadEpisodeAsync(
        string url,
        string destinationPath,
        bool skipExisting = true,
        CancellationToken cancellationToken = default);
}
