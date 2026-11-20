using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.Rezero.Api;
using Jellyfin.Plugin.Rezero.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Rezero.Tests;

public class ApiTests
{
    [Fact]
    public void VideosResponse_Deserialization_ShouldParseArcs()
    {
        var sampleJson = """
        {
          "arcs": [
            {
              "id": "arc01",
              "number": 1,
              "title": "The Tumultuous First Day",
              "anime_episodes": "S1 [1–3]",
              "description": "Subaru is suddenly transported to another world.",
              "artwork": "/juicy_prototype/images/arc1.jpg"
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<VideosResponse>(sampleJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        response.Should().NotBeNull();
        response!.Arcs.Should().HaveCount(1);
        response.Arcs[0].Id.Should().Be("arc01");
        response.Arcs[0].Number.Should().Be(1);
        response.Arcs[0].Title.Should().Be("The Tumultuous First Day");
    }

    [Fact]
    public void ResolveStreamUrl_DirectMp4_ShouldResolveCorrectly()
    {
        var client = new RezeroApiClient(new System.Net.Http.HttpClient(), NullLogger<RezeroApiClient>.Instance);
        var arc = new ArcDto { Id = "arc01", Number = 1 };
        var ep = new EpisodeDto
        {
            Number = 1,
            Sources = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
            {
                ["jp"] = new()
                {
                    ["cc"] = new()
                    {
                        ["1080p"] = "ARC%2001/EP%201/1080p/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC/index.m3u8"
                    }
                }
            }
        };

        var url = client.ResolveStreamUrl(ep, arc, "jp", "cc", "1080p", "direct_mp4");
        url.Should().Be("https://media.rezeroperfect.run/downloads/ARC%2001/EP%201/ARC%2001%20-%20EP%201%20-%20JP%20BURNED%20CC.mp4");
    }

    [Fact]
    public void ResolveStreamUrl_Hls_ShouldResolveCorrectly()
    {
        var client = new RezeroApiClient(new System.Net.Http.HttpClient(), NullLogger<RezeroApiClient>.Instance);
        var arc = new ArcDto { Id = "arc01", Number = 1 };
        var ep = new EpisodeDto
        {
            Number = 1,
            Sources = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
            {
                ["en"] = new()
                {
                    ["off"] = new()
                    {
                        ["720p"] = "ARC%2001/EP%201/720p/ARC%2001%20-%20EP%201%20-%20EN/index.m3u8"
                    }
                }
            }
        };

        var url = client.ResolveStreamUrl(ep, arc, "en", "off", "720p", "hls_m3u8");
        url.Should().Be("https://media.rezeroperfect.run/HLS/ARC%2001/EP%201/720p/ARC%2001%20-%20EP%201%20-%20EN/index.m3u8");
    }

    [Fact]
    public void UpdatesResponse_Deserialization_ShouldParseChanges()
    {
        var sampleJson = """
        {
          "updates": [
            {
              "date": "2026-11-16",
              "title": "Translations and feeds",
              "changes": [
                { "type": "added", "text": "RSS feeds enabled." }
              ]
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<UpdatesResponse>(sampleJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        response.Should().NotBeNull();
        response!.Updates.Should().HaveCount(1);
        response.Updates[0].Changes.Should().HaveCount(1);
        response.Updates[0].Changes[0].Type.Should().Be("added");
    }
}
