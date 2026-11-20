using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Rezero.Models;

/// <summary>
/// Root structure returned by videos.json.
/// </summary>
public class VideosResponse
{
    [JsonPropertyName("arcs")]
    public List<ArcDto> Arcs { get; set; } = new();
}

/// <summary>
/// Arc metadata descriptor.
/// </summary>
public class ArcDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("anime_episodes")]
    public string AnimeEpisodes { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("artwork")]
    public string Artwork { get; set; } = string.Empty;

    [JsonPropertyName("watch_artwork")]
    public string WatchArtwork { get; set; } = string.Empty;

    [JsonPropertyName("versions")]
    public Dictionary<string, string> Versions { get; set; } = new();
}

/// <summary>
/// Episode list container returned by episodes/{arcId}.json.
/// </summary>
public class EpisodeDataResponse
{
    [JsonPropertyName("episodes")]
    public List<EpisodeDto> Episodes { get; set; } = new();
}

/// <summary>
/// Individual episode entry.
/// </summary>
public class EpisodeDto
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("sources")]
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Sources { get; set; } = new();

    [JsonPropertyName("segments")]
    public EpisodeSegments? Segments { get; set; }
}

/// <summary>
/// Intro and outro segment timing markers.
/// </summary>
public class EpisodeSegments
{
    [JsonPropertyName("intro")]
    public SegmentRange? Intro { get; set; }

    [JsonPropertyName("outro")]
    public SegmentRange? Outro { get; set; }
}

/// <summary>
/// Start and end timestamps in seconds.
/// </summary>
public class SegmentRange
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }
}

/// <summary>
/// Root structure returned by updates.json.
/// </summary>
public class UpdatesResponse
{
    [JsonPropertyName("updates")]
    public List<UpdateEntryDto> Updates { get; set; } = new();
}

/// <summary>
/// Changelog entry.
/// </summary>
public class UpdateEntryDto
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("changes")]
    public List<UpdateChangeDto> Changes { get; set; } = new();
}

/// <summary>
/// Specific change description.
/// </summary>
public class UpdateChangeDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
