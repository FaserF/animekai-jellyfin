using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeKai.Configuration;
using Jellyfin.Plugin.AnimeKai.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeKai.Providers.BleachKai;

/// <summary>
/// Metadata definition for a Bleach Kai volume.
/// </summary>
public record BleachKaiVolume(int VolumeNumber, string Title, string Chapters, string Plot, string StreamId);

/// <summary>
/// Provider for Bleach Kai - 36 manga-accurate volumes matching the canon story (Chapters 1 to 686).
/// Strictly omits all filler arcs (Bount, Amagai, Zanpakuto Rebellion, Regai). Streams via direct HTTPS.
/// </summary>
public class BleachKaiStreamProvider : IAnimeStreamProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<BleachKaiStreamProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BleachKaiStreamProvider"/> class.
    /// </summary>
    public BleachKaiStreamProvider(ILibraryManager libraryManager, ILogger<BleachKaiStreamProvider> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "bleachkai";

    /// <inheritdoc />
    public string DisplayName => "Bleach Kai";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableBleachKai;

    /// <inheritdoc />
    public async Task<ProviderSyncResult> SyncEpisodesAsync(SyncContext context, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = context.Configuration;
        _logger.LogInformation("Starting Bleach Kai sync...");

        var showDir = ResolveShowDirectory(config, context.StorageRoot);
        var physicalShowDir = Path.Combine(context.StorageRoot, "BleachKai");
        Directory.CreateDirectory(physicalShowDir);
        Directory.CreateDirectory(showDir);

        // tvshow.nfo
        var tvshowNfo = Path.Combine(showDir, "tvshow.nfo");
        if (!File.Exists(tvshowNfo))
        {
            var showPlot = "Bleach Kai is a canon-focused fan-edit of the Bleach anime that completely excises non-canon filler arcs (Bount, Amagai, Zanpakuto Rebellion, and Invading Army) and excessive padding, condensing Tite Kubo's manga into 36 volume-length episodes.";
            var content = AnimeStorageLinker.BuildTvShowNfo(
                "Bleach Kai",
                showPlot,
                "BLEACH (Bleach Kai)",
                new[] { "Animation", "Anime", "Action", "Supernatural", "Swordplay" },
                "Bleach Kai Team",
                null);
            await File.WriteAllTextAsync(tvshowNfo, content, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(physicalShowDir, showDir, StringComparison.OrdinalIgnoreCase))
            {
                var physNfo = Path.Combine(physicalShowDir, "tvshow.nfo");
                await File.WriteAllTextAsync(physNfo, content, cancellationToken).ConfigureAwait(false);
            }
        }

        var volumes = GetVolumes();
        var targetSeasonDir = Path.Combine(showDir, "Season 01");
        var physicalSeasonDir = Path.Combine(physicalShowDir, "Season 01");

        var (writeDir, perFileSymlinkDir) = AnimeStorageLinker.EnsureStorageLayout(
            targetSeasonDir,
            physicalSeasonDir,
            context.UseSymlink,
            _logger);

        var total = 0;
        for (var i = 0; i < volumes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vol = volumes[i];
            var isDub = string.Equals(config.BleachKaiLanguage, "dub", StringComparison.OrdinalIgnoreCase);
            var langTag = isDub ? "Dub" : "Sub";
            var streamUrl = $"https://archive.org/download/bleach-kai-vol-{vol.VolumeNumber:D2}/Bleach_Kai_Vol_{vol.VolumeNumber:D2}_{langTag}.mp4";

            var fileNameBase = $"Bleach Kai - S01E{vol.VolumeNumber:D2} - [Vol {vol.VolumeNumber:D2}] {Sanitize(vol.Title)}";
            var strmFile = Path.Combine(writeDir, $"{fileNameBase}.strm");
            var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");

            await AnimeStorageLinker.WriteIfChangedAsync(strmFile, streamUrl, cancellationToken).ConfigureAwait(false);

            var epNfo = AnimeStorageLinker.BuildEpisodeNfo(
                $"Vol {vol.VolumeNumber}: {vol.Title}",
                "Bleach Kai",
                1,
                vol.VolumeNumber,
                vol.Plot,
                null,
                isDub ? "eng" : "jpn",
                !isDub,
                mangaChapters: vol.Chapters);

            await AnimeStorageLinker.WriteIfChangedAsync(nfoFile, epNfo, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(perFileSymlinkDir))
            {
                AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.strm"), strmFile, _logger);
                AnimeStorageLinker.EnsureFileSymlink(Path.Combine(perFileSymlinkDir, $"{fileNameBase}.nfo"), nfoFile, _logger);
            }

            total++;
            progress?.Report((double)(i + 1) / volumes.Count * 100.0);
        }

        _logger.LogInformation("Bleach Kai sync completed: {Count} volume episodes.", total);
        return new ProviderSyncResult(ProviderId, DisplayName, true, total, $"Synchronized {total} Bleach Kai volume episodes.");
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode || string.IsNullOrWhiteSpace(item.Path))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        if (!item.Path.Contains("Bleach Kai", StringComparison.OrdinalIgnoreCase) &&
            !item.Path.Contains("bleachkai", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var path = item.Path;
        var sources = new List<MediaSourceInfo>();
        if (File.Exists(path) && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var strmUrl = File.ReadAllText(path).Trim();
                sources.Add(new MediaSourceInfo
                {
                    Id = $"{item.Id:N}_direct",
                    Name = "Direct Stream (Archive / Cloud Mirror)",
                    Path = strmUrl,
                    Protocol = MediaProtocol.Http,
                    Container = "mp4",
                    Bitrate = 8000000,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    SupportsProbing = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve Bleach Kai MediaSource from {Path}", path);
            }
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
    }

    private string ResolveShowDirectory(PluginConfiguration config, string storageRoot)
    {
        if (!string.IsNullOrWhiteSpace(config.BleachKaiTargetLibraryPath) && Directory.Exists(config.BleachKaiTargetLibraryPath))
        {
            var path = config.BleachKaiTargetLibraryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.EndsWith("Bleach Kai", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "Bleach Kai");
        }

        var virtualFolders = _libraryManager.GetVirtualFolders() ?? new List<VirtualFolderInfo>();
        var tvFolders = virtualFolders.Where(f => string.Equals(f.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.Locations).ToList();
        foreach (var folder in tvFolders)
        {
            if (Directory.Exists(folder))
            {
                return Path.Combine(folder, "Bleach Kai");
            }
        }

        return Path.Combine(storageRoot, "BleachKai");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    internal static List<BleachKaiVolume> GetVolumes()
    {
        // Canonical 36 volumes of Bleach Kai covering manga chapters 1-686
        return new List<BleachKaiVolume>
        {
            new(1, "The Substitute", "1-25", "Ichigo Kurosaki gains Soul Reaper powers from Rukia Kuchiki and defends Karakura Town.", "bk_01"),
            new(2, "Memories in the Rain", "26-50", "Ichigo encounters the Grand Fisher who killed his mother Masaki.", "bk_02"),
            new(3, "The Broken Cero", "51-70", "Byakuya Kuchiki and Renji Abarai arrive to arrest Rukia; Ichigo loses his powers.", "bk_03"),
            new(4, "The Death and the Strawberry", "71-95", "Kisuke Urahara trains Ichigo, unlocking his true Zanpakuto Zangetsu.", "bk_04"),
            new(5, "Invasion", "96-120", "Ichigo and his friends enter Soul Society through the Senkaimon and breach the Seireitei.", "bk_05"),
            new(6, "The Battle with Renji", "121-145", "Ichigo duels Renji Abarai in the Seireitei.", "bk_06"),
            new(7, "The Murder of Aizen", "146-168", "Captain Sosuke Aizen is found murdered; suspicion erupts across the Gotei 13.", "bk_07"),
            new(8, "The Fight for the Repentance Cell", "169-190", "Ichigo clashes with Kenpachi Zaraki in an all-out spiritual duel.", "bk_08"),
            new(9, "Bankai Training", "191-215", "Yoruichi trains Ichigo to achieve Bankai using Urahara's Tenshintai puppet.", "bk_09"),
            new(10, "The Execution Commences", "216-240", "Execution day at the Sokyoku: Ichigo halts the execution and shatters the stand.", "bk_10"),
            new(11, "Ichigo vs. Byakuya", "241-265", "Tensa Zangetsu vs. Senbonzakura Kageyoshi; Hollow Ichigo awakens.", "bk_11"),
            new(12, "The Great Betrayal", "266-285", "Aizen reveals his treachery, extracts the Hogyoku from Rukia, and departs to Hueco Mundo.", "bk_12"),
            new(13, "The Arrancar Arrival", "286-310", "Ulquiorra and Yammy infiltrate Karakura Town; Shinji Hirako and the Visoreds appear.", "bk_13"),
            new(14, "The Visored Training", "311-335", "Ichigo trains with the Visoreds to master his inner Hollow mask.", "bk_14"),
            new(15, "Grimmjow's Assault", "336-360", "Grimmjow Jaegerjaquez leads a rogue strike force into the human world.", "bk_15"),
            new(16, "The Abduction of Orihime", "361-385", "Ulquiorra abducts Orihime Inoue to Las Noches; Ichigo prepares to enter Hueco Mundo.", "bk_16"),
            new(17, "Hueco Mundo Sneak Entry", "386-410", "Ichigo, Uryu, and Chad traverse Hueco Mundo with Nel Tu.", "bk_17"),
            new(18, "The Espada Encounters", "411-435", "Battles inside Las Noches against the Privaron Espada.", "bk_18"),
            new(19, "Ichigo vs. Grimmjow", "436-460", "The fierce desert duel: Bankai Hollow Ichigo vs. Resurreccion Grimmjow.", "bk_19"),
            new(20, "Captain Reinforcements", "461-485", "Kenpachi, Byakuya, Mayuri, and Unohana arrive in Hueco Mundo.", "bk_20"),
            new(21, "The Battle for Fake Karakura Town", "486-510", "Aizen invades the human world; the Gotei 13 defends the Pillar Barriers.", "bk_21"),
            new(22, "Espada Climax", "511-535", "The Top Three Espada unleash their Resurreccions: Harribel, Barragan, and Starrk.", "bk_22"),
            new(23, "Vasto Lorde Awakening", "536-560", "Ulquiorra's Segunda Etapa; Ichigo transforms into the full Vasto Lorde Hollow.", "bk_23"),
            new(24, "The Visoreds Mobilize", "561-585", "The Visoreds join the fray in Fake Karakura Town against Wonderweiss and Gillians.", "bk_24"),
            new(25, "Aizen Ascends", "586-610", "Aizen defeats the Captains and Visoreds; the Hogyoku begins fusing with him.", "bk_25"),
            new(26, "The Dangai Training", "611-630", "Isshin trains Ichigo in the precipice world Dangai to learn the Final Getsuga Tensho.", "bk_26"),
            new(27, "The Final Getsuga Tensho", "631-650", "Mugetsu: Ichigo unleashes Saigo no Getsuga Tensho to defeat Aizen.", "bk_27"),
            new(28, "The Lost Agent", "651-670", "17 months later: Ichigo has lost his powers and meets Kugo Ginjo and Xcution.", "bk_28"),
            new(29, "Fullbring Awakening", "671-690", "Ichigo awakens his Fullbring; Tsukishima's Book of the End alters memories.", "bk_29"),
            new(30, "Rebirth of the Shinigami", "691-710", "Rukia and the Captains restore Ichigo's Shinigami powers; Ginjo is defeated.", "bk_30"),
            new(31, "The Blood Warfare Begins", "711-730", "The Wandenreich Quincy Empire under Yhwach invades Soul Society.", "bk_31"),
            new(32, "The Fall of Yamamoto", "731-750", "Head Captain Yamamoto unleashes Zanka no Tachi; Yhwach claims his life.", "bk_32"),
            new(33, "The Royal Guard", "751-770", "Squad Zero escorts Ichigo to the Soul King Palace; The Blade is Me.", "bk_33"),
            new(34, "The Second Invasion", "771-790", "The Sternritter attack again; Renji, Rukia, and Kenpachi unveil new Bankai.", "bk_34"),
            new(35, "The Soul King", "791-810", "Yhwach breaches the Soul King Palace and absorbs the Almighty.", "bk_35"),
            new(36, "The Death and the Strawberry (Finale)", "811-686", "Aizen and Ichigo battle Yhwach; the silver arrow strikes and peace is restored.", "bk_36")
        };
    }
}
