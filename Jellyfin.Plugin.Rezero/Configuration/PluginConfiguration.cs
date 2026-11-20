using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Rezero.Configuration;

/// <summary>
/// Universal configuration settings for the Re:Zero Perfect Run plugin.
/// Allows customization for any environment (Special Season vs Standalone Series, local disk, network share, or cloud rclone mount).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the integration mode ("SpecialSeason" or "StandaloneSeries").
    /// </summary>
    public string IntegrationMode { get; set; } = "SpecialSeason";

    /// <summary>
    /// Gets or sets the series matching strategy ("AutoDetect" or "CustomPath").
    /// </summary>
    public string MatchingStrategy { get; set; } = "AutoDetect";

    /// <summary>
    /// Gets or sets a custom series path. When empty, automatically searches for Re:Zero in TV libraries.
    /// </summary>
    public string TargetSeriesPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target library folder for standalone series mode.
    /// </summary>
    public string StandaloneLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the standalone series title (default: "Re:Zero - The Perfect Run").
    /// </summary>
    public string StandaloneSeriesTitle { get; set; } = "Re:Zero - The Perfect Run";

    /// <summary>
    /// Gets or sets the season number for the Perfect Run release (default: 0 for Specials).
    /// </summary>
    public int SeasonNumber { get; set; } = 0;

    /// <summary>
    /// Gets or sets the folder name for the season (default: "Season 00").
    /// </summary>
    public string SeasonFolder { get; set; } = "Season 00";

    /// <summary>
    /// Gets or sets a value indicating whether all available video qualities (1080p, 720p, 480p)
    /// should dynamically be offered in Jellyfin client players via IMediaSourceProvider.
    /// </summary>
    public bool OfferAllQualitiesInPlayer { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether physical multi-version .strm files should be written on disk
    /// (e.g. - 1080p.strm, - 720p.strm, - 480p.strm) for native Jellyfin version grouping.
    /// </summary>
    public bool EnableMultiVersionFiles { get; set; } = false;

    /// <summary>
    /// Gets or sets the preferred audio language ("jp", "en", or "both").
    /// </summary>
    public string PreferredLanguage { get; set; } = "jp";

    /// <summary>
    /// Gets or sets the preferred caption mode ("cc", "off", or "both").
    /// </summary>
    public string PreferredCaptions { get; set; } = "cc";

    /// <summary>
    /// Gets or sets the default video stream quality ("1080p", "720p", "480p").
    /// </summary>
    public string PreferredQuality { get; set; } = "1080p";

    /// <summary>
    /// Gets or sets the stream format ("direct_mp4" or "hls_m3u8").
    /// Direct MP4 is recommended for universal Direct Play across web and TV apps.
    /// </summary>
    public string StreamFormat { get; set; } = "direct_mp4";

    /// <summary>
    /// Gets or sets the API base URL (customizable for local mirrors or proxies).
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://rezeroperfect.run/";

    /// <summary>
    /// Gets or sets the HLS CDN base URL.
    /// </summary>
    public string HlsBaseUrl { get; set; } = "https://media.rezeroperfect.run/HLS/";

    /// <summary>
    /// Gets or sets the Direct MP4 downloads base URL.
    /// </summary>
    public string DownloadsBaseUrl { get; set; } = "https://media.rezeroperfect.run/downloads/";

    /// <summary>
    /// Gets or sets a value indicating whether automated scheduled synchronization is enabled.
    /// </summary>
    public bool EnableAutoSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the synchronization interval in hours.
    /// </summary>
    public int SyncIntervalHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the identifier of the last processed release update.
    /// </summary>
    public string LastUpdateGuid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful synchronization.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the status message of the last sync run.
    /// </summary>
    public string LastSyncStatus { get; set; } = "Not yet synchronized";

    /// <summary>
    /// Gets or sets the total count of currently synchronized episodes.
    /// </summary>
    public int TotalSyncedEpisodes { get; set; } = 0;
}
