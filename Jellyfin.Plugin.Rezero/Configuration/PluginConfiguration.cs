using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Rezero.Configuration;

/// <summary>
/// Configuration settings for the Re:Zero Perfect Run plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether automated scheduled synchronization is enabled.
    /// </summary>
    public bool EnableAutoSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the synchronization interval in hours.
    /// </summary>
    public int SyncIntervalHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the preferred audio language ("jp", "en", or "both").
    /// </summary>
    public string PreferredLanguage { get; set; } = "jp";

    /// <summary>
    /// Gets or sets the preferred caption mode ("cc", "off", or "both").
    /// </summary>
    public string PreferredCaptions { get; set; } = "cc";

    /// <summary>
    /// Gets or sets the preferred video stream quality ("1080p", "720p", "480p").
    /// </summary>
    public string PreferredQuality { get; set; } = "1080p";

    /// <summary>
    /// Gets or sets the stream format ("direct_mp4" or "hls_m3u8").
    /// Direct MP4 is recommended for universal Direct Play across web and TV apps.
    /// </summary>
    public string StreamFormat { get; set; } = "direct_mp4";

    /// <summary>
    /// Gets or sets an optional custom series path. When empty, automatically searches for Re:Zero in TV libraries.
    /// </summary>
    public string TargetSeriesPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number for the Perfect Run release (default: 0 for Specials).
    /// </summary>
    public int SeasonNumber { get; set; } = 0;

    /// <summary>
    /// Gets or sets the folder name for the season (default: "Season 00").
    /// </summary>
    public string SeasonFolder { get; set; } = "Season 00";

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
