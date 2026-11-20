using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Rezero.Models;

namespace Jellyfin.Plugin.Rezero.Api;

/// <summary>
/// Interface for querying the Re:Zero Perfect Run JSON endpoints.
/// </summary>
public interface IRezeroApiClient
{
    /// <summary>
    /// Retrieves all available arcs and metadata.
    /// </summary>
    Task<VideosResponse?> GetArcsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves episode data for a specific arc ID.
    /// </summary>
    Task<EpisodeDataResponse?> GetArcEpisodesAsync(string arcId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the release changelog and updates.
    /// </summary>
    Task<UpdatesResponse?> GetUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the native download/torrent catalog from torrents.json.
    /// </summary>
    Task<TorrentManifestDto?> GetTorrentManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the optimal playback stream URL based on language, captions, and quality settings.
    /// </summary>
    string? ResolveStreamUrl(
        EpisodeDto episode,
        ArcDto arc,
        string language,
        string captions,
        string quality,
        string format);

    /// <summary>
    /// Resolves the native 1080p direct MP4 download URL for an episode.
    /// </summary>
    string? ResolveDownloadUrl(
        EpisodeDto episode,
        ArcDto arc,
        string language,
        string captions);
}
