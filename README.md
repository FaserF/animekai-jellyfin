<div align="center">

# 🌀 Jellyfin Re:Zero Perfect Run Plugin (`Jellyfin.Plugin.Rezero`)

**Seamlessly integrate *Re:Zero − Starting Life in Another World: The Perfect Run* into Jellyfin as an official Special Season with zero local storage overhead.**

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Jellyfin 10.10+ | 12.x](https://img.shields.io/badge/Jellyfin-10.10%2B%20%7C%2012.x-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org/)
[![Source: rezeroperfect.run](https://img.shields.io/badge/Source-rezeroperfect.run-8A2BE2)](https://rezeroperfect.run/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/FaserF/rezero-jellyfin?include_prereleases&color=blue)](https://github.com/FaserF/rezero-jellyfin/releases)

*Watch the acclaimed re-cut where Subaru succeeds on his first attempt without loops or deaths, integrated directly into your existing Re:Zero TV series with **zero-byte `.strm` direct playback**, **rich `.nfo` metadata**, **Intro Skipper segment markers**, and an **automated background update engine** that detects re-edits and newly released arcs.*

</div>

---

## 📖 Table of Contents

1. [Overview & Core Philosophy](#-overview--core-philosophy)
2. [System Architecture](#-system-architecture)
3. [Key Features Breakdown](#-key-features-breakdown)
   - [1. Official Special Season Integration (Season 00 / Specials)](#1-official-special-season-integration-season-00--specials)
   - [2. Zero Local Disk Overhead via `.strm` Direct Play](#2-zero-local-disk-overhead-via-strm-direct-play)
   - [3. Audio & Subtitle Synchronization (Japanese, English & Burned CC)](#3-audio--subtitle-synchronization)
   - [4. Automated Update Engine (Tracking `updates.json` & RSS)](#4-automated-update-engine)
   - [5. Native Intro Skipper Integration](#5-native-intro-skipper-integration)
4. [Configuration Reference (`PluginConfiguration`)](#-configuration-reference-pluginconfiguration)
5. [REST API & Scheduled Tasks](#-rest-api--scheduled-tasks)
6. [Local Build & Development](#-local-build--development)
7. [License & Sponsors](#-license--sponsors)

---

## 🌟 Overview & Core Philosophy

[Re:Zero - The Perfect Run](https://rezeroperfect.run) is a fan-made project that re-edits the entire *Re:Zero − Starting Life in Another World* anime to remove all deaths and loops, showing the narrative through the perspective of those around Subaru as he navigates the story with uncanny foresight.

Traditional integration requires downloading hundreds of gigabytes of video files. The **Jellyfin Re:Zero Plugin** eliminates storage bottlenecks:
- **No Video Downloads**: Uses lightweight `.strm` streaming pointers pointing to direct high-speed CDNs.
- **Official Look & Feel**: Automatically attaches to your existing `Re:Zero` series as **Season 00 (Specials)** (or creates a standalone series), complete with arc artwork, episode titles, and plot summaries.
- **Fast Local Dedicated Folder + Symlink**: Automatically detects mergerfs/rclone cloud mounts and stores tiny `.strm`/`.nfo` files (~500 KB total) on fast local disk (e.g. `/data/rezero/Season 00`), exposing them cleanly into your library via symlink with zero cloud API latency.
- **In-Player Quality Selection**: Offers 1080p Direct MP4, 720p HD, and 480p SD streams dynamically inside Jellyfin's Version/Quality dropdown.
- **100% API Powered**: Fetches structured metadata via clean JSON endpoints (`videos.json`, `episodes/arcXX.json`, and `updates.json`) with zero fragile web scraping.

---

## 🏗️ System Architecture

```mermaid
flowchart TD
    API["rezeroperfect.run API<br>(videos.json, arc01-06.json, updates.json)"] -->|HTTP / JSON| Client["RezeroApiClient"]
    Client --> SyncService["RezeroSyncService"]
    Task["RezeroSyncTask (Scheduled Task)"] -->|Periodic Check (Every 12h)| SyncService
    UI["Jellyfin Web Dashboard"] -->|Manual Sync Trigger| SyncService

    SyncService --> TargetSeries["Series & Mount Discovery<br>(StoragePathResolver)"]
    TargetSeries --> Writer["Strm & NFO Generator"]

    Writer --> StrmFiles[".strm Stream Files<br>(Universal 1080p Direct MP4 / HLS)"]
    Writer --> NfoFiles[".nfo Metadata Files<br>(Arc Info, Episode Plots, Intro/Outro)"]

    StrmFiles --> LocalStore["Dedicated Fast Local Folder<br>(e.g. /data/rezero/Season 00)"]
    NfoFiles --> LocalStore
    LocalStore -.->|Symlink| SeriesSeason["Series Folder<br>(.../Season 00/)"]

    SeriesSeason --> JellyfinServer["Jellyfin Media Server<br>(IMediaSourceProvider Multi-Quality)"]
    SyncService -->|Trigger| LibraryScan["Jellyfin Library Scanner"]
```

---

## ⚡ Key Features Breakdown

### 1. Official Special Season Integration (Season 00 / Specials)
The plugin scans Jellyfin libraries to locate your existing Re:Zero series (via TMDb ID `65942`, TVDb ID `305089`, or series folder matching). It creates or uses the `Season 00` folder and generates:
- `Re ZERO Starting Life in Another World - S00E01 - [Arc 01] The Tumultuous First Day (Part 1).strm`
- `Re ZERO Starting Life in Another World - S00E01 - [Arc 01] The Tumultuous First Day (Part 1).nfo`

All episodes are mapped sequentially across Arcs 1 through 6 (60 episodes in total).

### 2. Zero Local Disk Overhead via `.strm` Direct Play
Each `.strm` file contains a single HTTPS streaming URL:
- **Direct MP4** (`https://media.rezeroperfect.run/downloads/ARC%2001/EP%201/...mp4`): Plays directly in Apple TV, Android TV, Roku, Chrome, and Smart TVs without transcoding.
- **HLS** (`https://media.rezeroperfect.run/HLS/ARC%2001/.../index.m3u8`): Adaptive bitrate streaming.

### 3. Audio & Subtitle Synchronization
The plugin supports all four upstream permutations:
- **Japanese Audio with Burned Subtitles (`jp` + `cc`)** *(Default)*
- **English Audio with Burned Subtitles (`en` + `cc`)**
- **Clean Japanese Audio (`jp` + `off`)**
- **Clean English Audio (`en` + `off`)**
- **Both**: Automatically generates multi-version files (e.g. `[JP Sub]` and `[EN Dub]`).

### 4. Automated Update Engine
The upstream project regularly publishes re-edits, subtitle timing adjustments, and new arcs. The plugin:
- Polls `updates.json` every 12 hours (configurable).
- Tracks the latest `LastUpdateGuid`.
- Automatically re-generates `.strm` and `.nfo` files when an episode is marked as re-edited.

### 5. Native Intro Skipper Integration
Intro and outro segment timestamps from `episodes/arcXX.json` are serialized directly into the episode `.nfo` file:
```xml
<episodedetails>
  <title>[The Perfect Run] Arc 01: The Tumultuous First Day - Episode 1</title>
  <season>0</season>
  <episode>1</episode>
  <intro>
    <start>116</start>
    <end>206</end>
  </intro>
  <outro>
    <start>1344</start>
    <end>1434</end>
  </outro>
</episodedetails>
```
Jellyfin's **Intro Skipper** / **TheIntroDB** plugin instantly picks up these timestamps without needing audio fingerprinting!

---

## ⚙️ Configuration Reference (`PluginConfiguration`)

| Setting | Type | Default | Description |
|---|---|---|---|
| `IntegrationMode` | `string` | `"SpecialSeason"` | `"SpecialSeason"` or `"StandaloneSeries"`. |
| `SyncMode` | `string` | `"Stream"` | `"Stream"` (zero-overhead `.strm` pointers) or `"Download"` (sequential native 1080p MP4 downloads). |
| `MaxDownloadsPerRun` | `int` | `0` | Caps new MP4 downloads per sync run when `SyncMode` is `"Download"` (`0` = unlimited). |
| `SkipExistingDownloads` | `bool` | `true` | Skips MP4 download when local file size matches upstream `Content-Length`. |
| `CleanOppositeModeFiles` | `bool` | `true` | Removes matching `.strm` pointers after an MP4 download completes cleanly. |
| `TargetSeriesPath` | `string` | `""` | Custom path to Re:Zero series directory (auto-detected if blank). |
| `SeasonFolder` | `string` | `"Season 00"` | Subfolder inside series for the specials. |
| `EnableSymlink` | `bool` | `true` | Stores `.strm`/`.nfo` files in a dedicated local folder and exposes them via symlink. |
| `DedicatedStoragePath` | `string` | `""` | Optional manual dedicated local folder; blank enables dynamic auto-detection. |
| `OfferAllQualitiesInPlayer` | `bool` | `true` | Dynamically supplies 1080p/720p/480p options in Jellyfin player UI. |
| `EnableMultiVersionFiles` | `bool` | `false` | Writes separate physical `.strm` files per resolution on disk. |
| `EnableAutoSync` | `bool` | `true` | Enables background periodic synchronization. |
| `SyncIntervalHours` | `int` | `12` | Frequency of update checks (1–168 hours). |
| `PreferredLanguage` | `string` | `"jp"` | `"jp"`, `"en"`, or `"both"`. |
| `PreferredCaptions` | `string` | `"cc"` | `"cc"` (hardcoded subtitles), `"off"` (clean), or `"both"`. |
| `PreferredQuality` | `string` | `"1080p"` | Stream resolution (`"1080p"`, `"720p"`, `"480p"`). |
| `StreamFormat` | `string` | `"direct_mp4"` | `"direct_mp4"` or `"hls_m3u8"`. |

---

## 📡 REST API & Scheduled Tasks

### Plugin Repository Manifest URL
Add this repository URL in **Jellyfin Dashboard ➔ Plugins ➔ Manage Repositories**:
```text
https://raw.githubusercontent.com/FaserF/rezero-jellyfin/main/manifest.json
```

### REST Endpoints
- **`GET /Rezero/Status`**: Returns sync state, active `SyncMode`, resolved storage paths, and configuration.
- **`GET /Rezero/Updates`**: Fetches the live upstream changelog from `rezeroperfect.run`.
- **`GET /Rezero/Downloads`**: Fetches the native download & `torrents.json` catalog.
- **`POST /Rezero/Sync`**: Triggers immediate synchronization (supports optional `?syncMode=Stream|Download&maxDownloads=N&overrideSeriesPath=...` query overrides; requires Admin privileges).

### Scheduled Task
- Located in Jellyfin Dashboard under **Scheduled Tasks** ➔ **Live TV & Streaming** ➔ **Sync Re:Zero Perfect Run Episodes (.strm & .nfo)**.

---

## 🛠️ Local Build & Development

Run the Docker-based build script (no local .NET SDK required):
```powershell
./build-local.ps1
```

Or using the .NET CLI:
```bash
dotnet restore
dotnet build -c Release
dotnet test
```

---

## 📄 License & Sponsors

Licensed under the [MIT License](LICENSE).

Developed and maintained by **[@FaserF](https://github.com/FaserF)**.
- GitHub Sponsors: [faserf](https://github.com/sponsors/FaserF)
- PayPal: [paypal.me/FaserF](https://paypal.me/FaserF)
