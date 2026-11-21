using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

namespace Jellyfin.Plugin.AnimeKai.Providers.NarutoKai;

/// <summary>
/// Metadata definition for a Naruto Kai volume.
/// </summary>
public record NarutoKaiVolume(int VolumeNumber, string Title, string Chapters, string Plot, string StreamId);

/// <summary>
/// Provider for Naruto Kai - 72 feature-length, manga-accurate volume episodes matching Volumes 1 to 72.
/// Eliminates all anime filler arcs and padding. Streams via direct HTTPS with zero local storage overhead.
/// </summary>
public class NarutoKaiStreamProvider : IAnimeStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<NarutoKaiStreamProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NarutoKaiStreamProvider"/> class.
    /// </summary>
    public NarutoKaiStreamProvider(HttpClient httpClient, ILibraryManager libraryManager, ILogger<NarutoKaiStreamProvider> logger)
    {
        _httpClient = httpClient;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "narutokai";

    /// <inheritdoc />
    public string DisplayName => "Naruto Kai";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => config.EnableNarutoKai;

    /// <inheritdoc />
    public async Task<ProviderSyncResult> SyncEpisodesAsync(SyncContext context, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = context.Configuration;
        _logger.LogInformation("Starting Naruto Kai sync...");

        var showDir = ResolveShowDirectory(config, context.StorageRoot);
        Directory.CreateDirectory(showDir);

        // tvshow.nfo
        var tvshowNfo = Path.Combine(showDir, "tvshow.nfo");
        var showPlot = "Naruto Kai is a fan-edit that condenses the anime into 72 volume-length episodes corresponding to the 72 manga volumes created by Masashi Kishimoto. Every non-canon filler episode, recap, and repetitive flashback has been removed.";
        var content = AnimeStorageLinker.BuildTvShowNfo(
            "Naruto Kai",
            showPlot,
            "NARUTO (Naruto Kai)",
            new[] { "Animation", "Anime", "Action", "Adventure", "Martial Arts", "Fantasy" },
            "Naruto Kai Team",
            null,
            tvdbId: "78857");
        await AnimeStorageLinker.WriteIfChangedAsync(tvshowNfo, content, cancellationToken).ConfigureAwait(false);

        // Download official artwork from TVDB (anchors correct poster and banner)
        await AnimeStorageLinker.DownloadArtworkAsync(_httpClient, "https://artworks.thetvdb.com/banners/posters/78857-1.jpg", Path.Combine(showDir, "poster.jpg"), _logger, cancellationToken).ConfigureAwait(false);
        await AnimeStorageLinker.DownloadArtworkAsync(_httpClient, "https://artworks.thetvdb.com/banners/graphical/78857-g.jpg", Path.Combine(showDir, "banner.jpg"), _logger, cancellationToken).ConfigureAwait(false);
        await AnimeStorageLinker.DownloadArtworkAsync(_httpClient, "https://artworks.thetvdb.com/banners/fanart/original/78857-1.jpg", Path.Combine(showDir, "backdrop.jpg"), _logger, cancellationToken).ConfigureAwait(false);

        var volumes = GetVolumes();
        var seasonDir = Path.Combine(showDir, "Season 01");
        Directory.CreateDirectory(seasonDir);
        var writeDir = seasonDir;

        var total = 0;
        for (var i = 0; i < volumes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vol = volumes[i];
            var isDub = string.Equals(config.NarutoKaiLanguage, "dub", StringComparison.OrdinalIgnoreCase);
            var langTag = isDub ? "Dub" : "Sub";
            var apiBase = (string.IsNullOrWhiteSpace(config.ConsumetApiUrl) ? "http://consumet-proxy:3000" : config.ConsumetApiUrl).TrimEnd('/');
            var streamUrl = !string.IsNullOrWhiteSpace(config.NarutoKaiBaseStreamUrl)
                ? $"{config.NarutoKaiBaseStreamUrl.TrimEnd('/')}/Naruto_Kai_Vol_{vol.VolumeNumber:D2}_{langTag}.mp4"
                : $"{apiBase}/anime/narutokai/stream/{vol.VolumeNumber}/master.m3u8";

            var fileNameBase = $"Naruto Kai - S01E{vol.VolumeNumber:D2} - [Vol {vol.VolumeNumber:D2}] {Sanitize(vol.Title)}";
            var strmFile = Path.Combine(writeDir, $"{fileNameBase}.strm");
            var nfoFile = Path.Combine(writeDir, $"{fileNameBase}.nfo");

            await AnimeStorageLinker.WriteIfChangedAsync(strmFile, streamUrl, cancellationToken).ConfigureAwait(false);

            var epNfo = AnimeStorageLinker.BuildEpisodeNfo(
                $"{vol.Title} - [Vol {vol.VolumeNumber:D2}]",
                "Naruto Kai",
                1,
                vol.VolumeNumber,
                vol.Plot,
                null,
                isDub ? "eng" : "jpn",
                !isDub,
                mangaChapters: vol.Chapters);

            await AnimeStorageLinker.WriteIfChangedAsync(nfoFile, epNfo, cancellationToken).ConfigureAwait(false);

            total++;
            progress?.Report((double)(i + 1) / volumes.Count * 100.0);
        }

        _logger.LogInformation("Naruto Kai sync completed: {Count} volume episodes.", total);
        return new ProviderSyncResult(ProviderId, DisplayName, true, total, $"Synchronized {total} Naruto Kai volume episodes.");
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode || string.IsNullOrWhiteSpace(item.Path))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        if (!item.Path.Contains("Naruto Kai", StringComparison.OrdinalIgnoreCase) &&
            !item.Path.Contains("narutokai", StringComparison.OrdinalIgnoreCase))
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
                var isHls = strmUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || strmUrl.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase);
                sources.Add(new MediaSourceInfo
                {
                    Id = $"{item.Id:N}_direct",
                    Name = isHls ? "Adaptive HLS (Consumet Gateway)" : "Direct Stream (Archive / Cloud Mirror)",
                    Path = strmUrl,
                    Protocol = MediaProtocol.Http,
                    Container = isHls ? "m3u8" : "mp4",
                    Bitrate = 8000000,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    SupportsProbing = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve Naruto Kai MediaSource from {Path}", path);
            }
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
    }

    private string ResolveShowDirectory(PluginConfiguration config, string storageRoot)
        => AnimeStorageLinker.ResolveShowDirectory(config, _libraryManager, "Naruto Kai", config.NarutoKaiTargetLibraryPath, storageRoot, "NarutoKai");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    internal static List<NarutoKaiVolume> GetVolumes()
    {
        // Canonical 72 manga volumes matching Kishimoto's manga
        return new List<NarutoKaiVolume>
        {
            new(1, "Uzumaki Naruto", "1-4", "Naruto graduates the Ninja Academy, learns Shadow Clones, and joins Team 7 with Sasuke and Sakura under Kakashi.", "nk_01"),
            new(2, "The Worst Client", "5-17", "Team 7 undertakes a C-rank mission to escort Tazuna to the Land of Waves and faces Zabuza Momochi.", "nk_02"),
            new(3, "For the Sake of Dreams", "18-27", "The climax on the Great Naruto Bridge: Naruto unlocks Nine-Tails chakra against Haku.", "nk_03"),
            new(4, "The Bridge of the Hero", "28-36", "Team 7 enters the Chunin Exams in the Hidden Leaf Village.", "nk_04"),
            new(5, "The Challengers", "37-45", "The Forest of Death survival test begins; Team 7 encounters Orochimaru.", "nk_05"),
            new(6, "Sakura's Determination", "46-54", "Sakura defends her teammates, and the preliminary tournament commences.", "nk_06"),
            new(7, "The Path to Follow", "55-63", "The Chunin preliminary matches continue in the Tower.", "nk_07"),
            new(8, "Life-or-Death Battles", "64-72", "Rock Lee unleashes the Eight Gates against Gaara.", "nk_08"),
            new(9, "Neji and Hinata", "73-81", "The Hyuga clan battle concludes, and Naruto begins training under Jiraiya.", "nk_09"),
            new(10, "A Splendid Ninja", "82-90", "Naruto signs the Toad Summoning contract and summons Gamabunta.", "nk_10"),
            new(11, "The Finals Begin", "91-99", "The Chunin Exams finals: Naruto vs. Neji and Sasuke vs. Gaara.", "nk_11"),
            new(12, "The Great Leaf Crisis", "100-108", "Orochimaru's invasion of Konoha begins; the Third Hokage battles Orochimaru.", "nk_12"),
            new(13, "Chunin Exam Conclusion", "109-117", "Naruto faces Gaara and the One-Tail Shukaku in the forest.", "nk_13"),
            new(14, "Hokage vs. Hokage", "118-126", "Third Hokage uses the Reaper Death Seal to seal Orochimaru's arms.", "nk_14"),
            new(15, "Naruto's Ninja Handbook", "127-135", "Naruto defeats Gaara and shows him the true meaning of strength.", "nk_15"),
            new(16, "Konoha Destruction Climax", "136-144", "The aftermath of the invasion; Jiraiya and Naruto depart to find Tsunade.", "nk_16"),
            new(17, "Itachi's Return", "145-153", "Itachi Uchiha and Kisame Hoshigaki infiltrate Konoha; Sasuke confronts Itachi.", "nk_17"),
            new(18, "Tsunade's Choice", "154-162", "Naruto begins learning the Rasengan while Orochimaru tries to persuade Tsunade.", "nk_18"),
            new(19, "The Successor", "163-171", "The Three Legendary Sannin showdown: Jiraiya, Tsunade, and Orochimaru.", "nk_19"),
            new(20, "Naruto vs. Sasuke", "172-180", "Tsunade becomes the Fifth Hokage; rooftop duel between Naruto and Sasuke.", "nk_20"),
            new(21, "The Sound Four", "181-189", "Sasuke defects to Orochimaru escorted by the Sound Four.", "nk_21"),
            new(22, "Sasuke Retrieval Begins", "190-198", "Shikamaru leads the retrieval squad; Choji battles Jirobo.", "nk_22"),
            new(23, "Tough Fight", "199-207", "Neji fights Kidomaru in a deadly sniper battle.", "nk_23"),
            new(24, "Crisis, Crisis, Crisis", "208-217", "Kiba and Akamaru fight Sakon; Shikamaru battles Tayuya.", "nk_24"),
            new(25, "Itachi and Sasuke", "218-228", "The Sand Siblings arrive as reinforcements; Rock Lee fights Kimimaro.", "nk_25"),
            new(26, "The Day of Awakening", "229-238", "Naruto confronts Sasuke at the Valley of the End.", "nk_26"),
            new(27, "The Day of Departure", "239-244", "Final clash between Nine-Tails Naruto and Curse Mark Sasuke; Part 1 concludes.", "nk_27"),
            new(28, "Homecoming", "245-253", "Part 2 (Shippuden) begins: older Naruto returns to Konoha and re-teams with Kakashi.", "nk_28"),
            new(29, "Kakashi vs. Itachi", "254-262", "The Akatsuki extracts Shukaku from Gaara; Team 7 encounters Itachi clone.", "nk_29"),
            new(30, "Puppet Master Battle", "263-271", "Sakura and Granny Chiyo battle Sasori of the Red Sand.", "nk_30"),
            new(31, "Entrusted Feelings", "272-281", "Chiyo revives Gaara using her life-force jutsu.", "nk_31"),
            new(32, "The Road to Sasuke", "282-292", "Team Yamato is formed with Sai; Tenchi Bridge reconnaissance begins.", "nk_32"),
            new(33, "Top Secret Mission", "293-302", "Four-Tails Naruto battles Orochimaru; Sai infiltrates Orochimaru's hideout.", "nk_33"),
            new(34, "The Power of Reunion", "303-313", "Team Yamato reunites with Sasuke in Orochimaru's lair.", "nk_34"),
            new(35, "The New Two", "314-323", "Naruto begins Wind Style chakra nature training under Kakashi and Yamato.", "nk_35"),
            new(36, "Cell Number 10", "324-333", "Hidan and Kakuzu battle Team Asuma; Asuma's tragic sacrifice.", "nk_36"),
            new(37, "Shikamaru's Battle", "334-342", "Shikamaru outsmarts Hidan; Naruto arrives with the Wind Style: Rasenshuriken.", "nk_37"),
            new(38, "Practice Makes Perfect", "343-352", "Sasuke absorbs Orochimaru and forms the Hebi squad (Suigetsu, Karin, Jugo).", "nk_38"),
            new(39, "Those Who Move Forward", "353-363", "Sasuke battles Deidara; Deidara's ultimate suicide art.", "nk_39"),
            new(40, "The Ultimate Art", "364-373", "Jiraiya infiltrates the Hidden Rain Village to confront Pain.", "nk_40"),
            new(41, "Jiraiya's Choice", "374-383", "Jiraiya unlocks Sage Mode against the Six Paths of Pain; Jiraiya's final testament.", "nk_41"),
            new(42, "The Secret of the Mangekyo", "384-393", "The long-awaited brothers' duel: Sasuke vs. Itachi.", "nk_42"),
            new(43, "The Truth", "394-402", "Tobi reveals the hidden truth about Itachi's sacrifice to Sasuke.", "nk_43"),
            new(44, "Senjutsu Heir", "403-412", "Sasuke attacks Killer Bee; Naruto begins Sage Mode training at Mount Myoboku.", "nk_44"),
            new(45, "Battlefield, Konoha", "413-422", "Pain invades Konoha and destroys the village with Almighty Push.", "nk_45"),
            new(46, "Naruto's Return", "423-432", "Sage Naruto arrives on giant toads to fight the Six Paths of Pain.", "nk_46"),
            new(47, "Seal Broken", "433-442", "Hinata protects Naruto; Naruto meets Minato Namikaze inside his psyche.", "nk_47"),
            new(48, "The Cheering Village", "443-453", "Naruto confronts Nagato, inspiring him to cast the Samsara Heavenly Life jutsu.", "nk_48"),
            new(49, "Five Kage Summit", "454-463", "The Five Kage convene in the Land of Iron; Sasuke attacks the summit.", "nk_49"),
            new(50, "Mortal Combat in the Land of Iron", "464-473", "Sasuke vs. the Raikage; Danzo flees as Tobi declares the Fourth Great Ninja War.", "nk_50"),
            new(51, "Sasuke vs. Danzo", "474-483", "Sasuke kills Danzo with Susanoo; Team 7 has a brief, tense encounter.", "nk_51"),
            new(52, "Cell 7 Reunion", "484-493", "Naruto promises Sasuke they will settle their destiny together.", "nk_52"),
            new(53, "The Birth of Naruto", "494-504", "Naruto meets his mother Kushina and masters the Nine-Tails' chakra.", "nk_53"),
            new(54, "Peace Bridge", "505-514", "Konan vs. Tobi; Kabuto allies with Tobi using the Reanimation Jutsu.", "nk_54"),
            new(55, "The Great War Begins", "515-524", "The Allied Shinobi Forces mobilize against the White Zetsu Army and reanimated ninja.", "nk_55"),
            new(56, "Team Asuma Reunited", "525-534", "The Ino-Shika-Cho trio faces their reanimated sensei Asuma.", "nk_56"),
            new(57, "Naruto to the Battlefield", "535-544", "Naruto and Killer Bee break through the barrier to join the frontline.", "nk_57"),
            new(58, "Naruto vs. Itachi", "545-554", "Itachi breaks free of Kabuto's control using Shisui's Kotoamatsukami eye.", "nk_58"),
            new(59, "The Five Kage Gather", "555-564", "The real Madara Uchiha is reanimated and devastates the Fourth Division.", "nk_59"),
            new(60, "Kurama", "565-574", "Naruto learns Kurama's real name and achieves complete Bijuu Mode harmony.", "nk_60"),
            new(61, "Brotherly Tag Team", "575-584", "Itachi and Sasuke team up to defeat Kabuto and release the Reanimation Jutsu.", "nk_61"),
            new(62, "I Will Always Love You", "585-594", "Itachi's final goodbye to Sasuke; the mask of Tobi is cracked.", "nk_62"),
            new(63, "World of Dreams", "595-607", "Tobi is unmasked as Obito Uchiha; the Ten-Tails is resurrected.", "nk_63"),
            new(64, "The Ten-Tails", "608-617", "Neji sacrifices himself to protect Naruto and Hinata.", "nk_64"),
            new(65, "Hashirama and Madara", "618-627", "Orochimaru reanimates the First Four Hokage; Hashirama shares Konoha's origin.", "nk_65"),
            new(66, "The New Three-Way Deadlock", "628-637", "Sasuke and the Four Hokage arrive at the battlefield; Team 7 reunites.", "nk_66"),
            new(67, "A Breakthrough", "638-647", "Obito becomes the Ten-Tails Jinchuriki.", "nk_67"),
            new(68, "Paths of Destiny", "648-657", "Naruto and Sasuke defeat Obito; Madara fully resurrects via Rinnegan.", "nk_68"),
            new(69, "The Crimson Spring", "658-667", "Might Guy opens the Eighth Gate of Death against Six Paths Madara.", "nk_69"),
            new(70, "Naruto and the Sage of Six Paths", "668-677", "Hagoromo Otsutsuki grants Naruto and Sasuke Six Paths power.", "nk_70"),
            new(71, "I Love You", "678-689", "Infinite Tsukuyomi is cast; Black Zetsu betrays Madara to revive Kaguya Otsutsuki.", "nk_71"),
            new(72, "Uzumaki Naruto!!", "690-700", "Team 7 seals Kaguya; the final battle between Naruto and Sasuke at the Valley of the End.", "nk_72")
        };
    }
}
