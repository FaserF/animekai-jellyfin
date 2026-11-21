using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace Jellyfin.Plugin.AnimeKai.Services;

/// <summary>
/// NFO XML metadata serialization helper for anime fan-edits and recuts.
/// </summary>
internal static class AnimeNfoBuilder
{
    internal static string BuildTvShowNfo(
        string title,
        string plot,
        string? originalTitle = null,
        IEnumerable<string>? genres = null,
        string? studio = null,
        string? posterUrl = null,
        string? tvdbId = null)
    {
        var elements = new List<object>
        {
            new XElement("title", title),
            new XElement("originaltitle", originalTitle ?? title),
            new XElement("showtitle", title),
            new XElement("plot", plot),
            new XElement("studio", studio ?? "Anime Kai & Fan-Edits"),
            new XElement("status", "Continuing"),
            new XElement("lockdata", "true")
        };

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            elements.Add(new XElement("uniqueid", new XAttribute("type", "tvdb"), new XAttribute("default", "true"), tvdbId));
            elements.Add(new XElement("tvdbid", tvdbId));
        }

        if (genres != null)
        {
            foreach (var g in genres)
            {
                elements.Add(new XElement("genre", g));
            }
        }
        else
        {
            elements.Add(new XElement("genre", "Animation"));
            elements.Add(new XElement("genre", "Anime"));
            elements.Add(new XElement("genre", "Action"));
        }

        if (IsValidImageUrl(posterUrl))
        {
            elements.Add(new XElement("thumb", new XAttribute("aspect", "poster"), posterUrl));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("tvshow", elements));

        return doc.Declaration + Environment.NewLine + doc;
    }

    internal static string BuildEpisodeNfo(
        string title,
        string showTitle,
        int seasonNumber,
        int episodeNumber,
        string? plot,
        string? thumbUrl,
        string lang = "jpn",
        bool hasSubtitles = true,
        int? introStart = null,
        int? introEnd = null,
        int? outroStart = null,
        int? outroEnd = null,
        string? mangaChapters = null)
    {
        var streamDetails = new XElement("streamdetails",
            new XElement("video",
                new XElement("codec", "h264"),
                new XElement("width", "1920"),
                new XElement("height", "1080"),
                new XElement("aspect", "16:9")),
            new XElement("audio",
                new XElement("codec", "aac"),
                new XElement("language", lang),
                new XElement("channels", "2")));

        if (hasSubtitles)
        {
            streamDetails.Add(new XElement("subtitle",
                new XElement("language", "eng"),
                new XElement("forced", "true")));
        }

        var elements = new List<object>
        {
            new XElement("title", title),
            new XElement("originaltitle", title),
            new XElement("showtitle", showTitle),
            new XElement("season", seasonNumber),
            new XElement("episode", episodeNumber),
            new XElement("plot", plot ?? string.Empty),
            new XElement("lockdata", "true")
        };

        if (!string.IsNullOrWhiteSpace(mangaChapters))
        {
            elements.Add(new XElement("tag", $"Manga Chapters: {mangaChapters}"));
        }

        if (IsValidImageUrl(thumbUrl))
        {
            elements.Add(new XElement("thumb", thumbUrl));
        }

        if (introStart.HasValue && introEnd.HasValue)
        {
            elements.Add(new XElement("intro",
                new XElement("start", introStart.Value),
                new XElement("end", introEnd.Value)));
        }

        if (outroStart.HasValue && outroEnd.HasValue)
        {
            elements.Add(new XElement("outro",
                new XElement("start", outroStart.Value),
                new XElement("end", outroEnd.Value)));
        }

        elements.Add(new XElement("fileinfo", streamDetails));

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("episodedetails", elements));

        return doc.Declaration + Environment.NewLine + doc;
    }

    internal static string BuildSeasonNfo(string title, int seasonNumber, string? plot = null)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("season",
                new XElement("title", title),
                new XElement("seasonnumber", seasonNumber),
                new XElement("plot", plot ?? string.Empty),
                new XElement("lockdata", "true")));
        return doc.Declaration + Environment.NewLine + doc;
    }

    internal static bool IsValidImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (url.Contains("archive.org/download/") ||
            url.Contains("onepace.net/images/"))
        {
            return false;
        }

        return true;
    }
}
