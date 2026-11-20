using Jellyfin.Plugin.Rezero.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Rezero;

/// <summary>
/// Main plugin instance for Re:Zero Perfect Run integration.
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
    public override string Name => "Re:Zero Perfect Run";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("aaf63d7c-95e2-4cda-a6fc-7fbea2b90f8a");

    /// <inheritdoc />
    public override string Description =>
        "Seamlessly integrates Re:Zero - The Perfect Run into Jellyfin as an official-grade Special Season with zero local storage overhead via dynamic .strm and .nfo streaming synchronization.";

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
