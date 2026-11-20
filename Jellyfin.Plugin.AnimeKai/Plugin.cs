using Jellyfin.Plugin.AnimeKai.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AnimeKai;

/// <summary>
/// Main plugin instance for Anime Kai &amp; Fan-Edits universal integration.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Anime Kai & Fan-Edits";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("aaf63d7c-95e2-4cda-a6fc-7fbea2b90f8a");

    /// <inheritdoc />
    public override string Description =>
        "Universal streaming & metadata synchronization plugin for Anime Fan-Edits and Encrypted Streams: Re:Zero Perfect Run, One Pace, Naruto Kai, Bleach Kai, and Consumet HLS Gateway.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
