using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeKai.Configuration;

/// <summary>
/// Universal configuration settings for Anime Kai &amp; Fan-Edits plugin.
/// Supports multi-provider synchronization for Re:Zero Perfect Run, One Pace, Naruto Kai, Bleach Kai, and Consumet HLS Gateway.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ==========================================
    // Global & Storage Settings
    // ==========================================

    /// <summary>
    /// Gets or sets an optional manual dedicated storage path where .strm and .nfo files are physically stored
    /// (e.g. /data/animekai on fast local SSD). Leave blank (default) for dynamic auto-detection.
    /// </summary>
    public string DedicatedStoragePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the dedicated anime storage root (e.g. /data/animekai)
    /// should be automatically registered as a media path in Jellyfin's primary TV library ("Serien").
    /// Eliminates the need for symlinks and MergerFS cross-filesystem overhead.
    /// </summary>
    public bool AutoRegisterLibraryPath { get; set; } = true;

    /// <summary>
    /// Gets or sets the global default target TV library root folder (e.g. /data/tv on Google Drive / MergerFS pool)
    /// where all active providers create show directories. Leave blank to use dedicated storage path.
    /// </summary>
    public string DefaultTargetLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether legacy symlinks into TV library are enabled.
    /// Defaults to false in favor of direct dedicated library storage.
    /// </summary>
    public bool EnableSymlink { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether automated background synchronization is enabled.
    /// </summary>
    public bool EnableAutoSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the synchronization interval in hours.
    /// </summary>
    public int SyncIntervalHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful synchronization.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the status message of the last sync run.
    /// </summary>
    public string LastSyncStatus { get; set; } = "Not yet synchronized";

    /// <summary>
    /// Gets or sets the total count of currently synchronized episodes across all active providers.
    /// </summary>
    public int TotalSyncedEpisodes { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether all qualities should be exposed in Jellyfin's version selector.
    /// </summary>
    public bool OfferAllQualitiesInPlayer { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether opposite-mode generated .strm pointers should be removed after an MP4 is downloaded.
    /// </summary>
    public bool CleanOppositeModeFiles { get; set; } = true;

    // ==========================================
    // Provider 1: Re:Zero - The Perfect Run
    // ==========================================

    /// <summary>
    /// Gets or sets a value indicating whether Re:Zero - The Perfect Run integration is enabled.
    /// Default is false (user explicitly enables in dashboard).
    /// </summary>
    public bool EnableRezero { get; set; } = false;

    /// <summary>
    /// Gets or sets the Re:Zero integration mode ("SpecialSeason" or "StandaloneSeries").
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
    /// Gets or sets a value indicating whether multi-version files (qualities &amp; language variants) should be generated.
    /// </summary>
    public bool EnableMultiVersionFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets the preferred language for audio ("jp", "en", or "both").
    /// </summary>
    public string PreferredLanguage { get; set; } = "both";

    /// <summary>
    /// Gets or sets the preferred caption mode ("clean", "cc", or "both").
    /// </summary>
    public string PreferredCaptions { get; set; } = "both";

    /// <summary>
    /// Gets or sets the preferred video resolution ("1080p", "720p", "480p").
    /// </summary>
    public string PreferredQuality { get; set; } = "1080p";

    /// <summary>
    /// Gets or sets the stream format ("hls_direct" [Default, instant single-variant HLS], "hls_auto" [Adaptive bitrate master], or "direct_mp4").
    /// </summary>
    public string StreamFormat { get; set; } = "hls_direct";

    /// <summary>
    /// Gets or sets the API base URL.
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
    /// Gets or sets the Torrents CDN base URL (HTTP metadata only, zero P2P).
    /// </summary>
    public string TorrentsBaseUrl { get; set; } = "https://media.rezeroperfect.run/torrents/";

    /// <summary>
    /// Gets or sets the synchronization mode ("Stream" [Default] or "Download").
    /// </summary>
    public string SyncMode { get; set; } = "Stream";

    /// <summary>
    /// Gets or sets the maximum number of new MP4 downloads per sync run in Download mode (0 = unlimited).
    /// </summary>
    public int MaxDownloadsPerRun { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether already-downloaded MP4 files matching remote size should be skipped.
    /// </summary>
    public bool SkipExistingDownloads { get; set; } = true;

    /// <summary>
    /// Gets or sets the identifier of the last processed release update.
    /// </summary>
    public string LastUpdateGuid { get; set; } = string.Empty;

    // ==========================================
    // Provider 2: One Pace (onepace.net)
    // ==========================================

    /// <summary>
    /// Gets or sets a value indicating whether One Pace integration is enabled.
    /// Default is false.
    /// </summary>
    public bool EnableOnePace { get; set; } = false;

    /// <summary>
    /// Gets or sets the One Pace integration mode ("StandaloneSeries" [Default] or "SpecialSeason").
    /// </summary>
    public string OnePaceIntegrationMode { get; set; } = "StandaloneSeries";

    /// <summary>
    /// Gets or sets a custom series directory for One Pace. Leave empty to auto-detect anime library folder.
    /// </summary>
    public string OnePaceTargetLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred language for One Pace ("sub" [Japanese + English Subs], "dub" [English Dub], "dub_cc" [English Dub + CC]).
    /// </summary>
    public string OnePaceLanguage { get; set; } = "sub";

    /// <summary>
    /// Gets or sets the preferred video resolution for One Pace (1080, 720, 480).
    /// </summary>
    public int OnePacePreferredQuality { get; set; } = 1080;

    /// <summary>
    /// Gets or sets a value indicating whether Extended Cuts should be preferred when available.
    /// </summary>
    public bool OnePacePreferExtendedCut { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional Pixeldrain API Key for higher bandwidth limits.
    /// </summary>
    public string OnePacePixeldrainApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the One Pace watch catalog URL.
    /// </summary>
    public string OnePaceWatchPageUrl { get; set; } = "https://onepace.net/en/watch";

    // ==========================================
    // Provider 3: Naruto Kai
    // ==========================================

    /// <summary>
    /// Gets or sets a value indicating whether Naruto Kai integration is enabled.
    /// Default is false.
    /// </summary>
    public bool EnableNarutoKai { get; set; } = false;

    /// <summary>
    /// Gets or sets the Naruto Kai integration mode ("StandaloneSeries" [Default] or "SpecialSeason").
    /// </summary>
    public string NarutoKaiIntegrationMode { get; set; } = "StandaloneSeries";

    /// <summary>
    /// Gets or sets a custom directory for Naruto Kai. Leave empty to auto-detect anime library folder.
    /// </summary>
    public string NarutoKaiTargetLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred audio language for Naruto Kai ("sub" or "dub").
    /// </summary>
    public string NarutoKaiLanguage { get; set; } = "sub";

    /// <summary>
    /// Gets or sets an optional custom base URL for Naruto Kai episodes (e.g. https://mycdn.example.com/narutokai/).
    /// </summary>
    public string NarutoKaiBaseStreamUrl { get; set; } = string.Empty;

    // ==========================================
    // Provider 4: Bleach Kai
    // ==========================================

    /// <summary>
    /// Gets or sets a value indicating whether Bleach Kai integration is enabled.
    /// Default is false.
    /// </summary>
    public bool EnableBleachKai { get; set; } = false;

    /// <summary>
    /// Gets or sets the Bleach Kai integration mode ("StandaloneSeries" [Default] or "SpecialSeason").
    /// </summary>
    public string BleachKaiIntegrationMode { get; set; } = "StandaloneSeries";

    /// <summary>
    /// Gets or sets a custom directory for Bleach Kai. Leave empty to auto-detect anime library folder.
    /// </summary>
    public string BleachKaiTargetLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred audio language for Bleach Kai ("sub" or "dub").
    /// </summary>
    public string BleachKaiLanguage { get; set; } = "sub";

    /// <summary>
    /// Gets or sets an optional custom base URL for Bleach Kai episodes (e.g. https://mycdn.example.com/bleachkai/).
    /// </summary>
    public string BleachKaiBaseStreamUrl { get; set; } = string.Empty;

    // ==========================================
    // Provider 5: Consumet / AniCli HLS Gateway
    // ==========================================

    /// <summary>
    /// Gets or sets a value indicating whether Consumet HLS Gateway is enabled.
    /// Default is false.
    /// </summary>
    public bool EnableConsumet { get; set; } = false;

    /// <summary>
    /// Gets or sets the Consumet integration mode ("DedicatedLibrary" [Default] or "Integrated").
    /// </summary>
    public string ConsumetIntegrationMode { get; set; } = "DedicatedLibrary";

    /// <summary>
    /// Gets or sets the base URL for the Consumet API container (default: http://consumet-proxy:3000 on docker network).
    /// </summary>
    public string ConsumetApiUrl { get; set; } = "http://consumet-proxy:3000";

    /// <summary>
    /// Gets or sets the scraper provider name inside Consumet (default: "hianime", options: "zoro", "gogoanime", "animepahe").
    /// </summary>
    public string ConsumetProvider { get; set; } = "hianime";

    /// <summary>
    /// Gets or sets the target library folder for Consumet on-demand streaming series.
    /// </summary>
    public string ConsumetTargetLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets comma-separated subscribed anime show slugs to synchronize (e.g. "frieren-beyond-journeys-end,steins-gate").
    /// </summary>
    public string ConsumetSubscribedShows { get; set; } = string.Empty;

    /// <summary>
    /// Clones this configuration.
    /// </summary>
    /// <returns>A memberwise clone of this configuration.</returns>
    internal PluginConfiguration Clone() => (PluginConfiguration)MemberwiseClone();
}
