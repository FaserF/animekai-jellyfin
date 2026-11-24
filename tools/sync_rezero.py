#!/usr/bin/env python3
"""
Re:Zero Perfect Run - Streaming (.strm) & Metadata (.nfo) Synchronizer.

Fetches structured release and episode data from https://rezeroperfect.run JSON endpoints
and generates Jellyfin-compliant .strm stream pointers and rich XML .nfo files
for Season 00 (Specials).

Usage:
    python sync_rezero.py --out-dir "C:/path/to/Season 00"
    python sync_rezero.py --rclone-target "cloud_pool_crypt:tv/Re - ZERO, Starting Life in Another World/Season 00"
"""

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET

SITE_BASE = "https://rezeroperfect.run"
DOWNLOADS_BASE = "https://media.rezeroperfect.run/downloads"
HLS_BASE = "https://media.rezeroperfect.run/HLS"


def fetch_json(url: str):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Rezero-Jellyfin-Sync/1.0)"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def sanitize_filename(name: str) -> str:
    return re.sub(r'[\\/*?:"<>|]', "_", name).strip()


def resolve_stream_url(episode: dict, arc: dict, language: str = "jp", captions: str = "cc",
                       quality: str = "1080p", format_type: str = "direct_mp4") -> str:
    sources = episode.get("sources", {})
    lang_dict = sources.get(language, {})
    cap_dict = lang_dict.get(captions, {})
    rel_path = cap_dict.get(quality) or cap_dict.get("1080p") or cap_dict.get("720p")
    if not rel_path:
        return ""

    if format_type == "direct_mp4":
        parts = rel_path.split("?")[0].split("/")
        if len(parts) >= 5 and parts[-1] == "index.m3u8":
            arc_part = parts[-5]
            ep_part = parts[-4]
            name_part = parts[-2]
            return f"{DOWNLOADS_BASE}/{arc_part}/{ep_part}/{name_part}.mp4"

    return f"{HLS_BASE}/{rel_path}" if not rel_path.startswith("http") else rel_path


def build_nfo(arc: dict, episode: dict, global_index: int, season_num: int, ep_title: str, suffix: str) -> str:
    root = ET.Element("episodedetails")
    ET.SubElement(root, "title").text = f"[The Perfect Run] Arc {arc['number']:02d}: {arc['title']} - Episode {episode['number']}{suffix}"
    ET.SubElement(root, "showtitle").text = "Re: ZERO, Starting Life in Another World"
    ET.SubElement(root, "season").text = str(season_num)
    ET.SubElement(root, "episode").text = str(global_index)
    ET.SubElement(root, "plot").text = arc.get("description", "")
    ET.SubElement(root, "thumb").text = f"{SITE_BASE}{arc.get('artwork', '')}"

    segments = episode.get("segments", {})
    if segments.get("intro"):
        intro = ET.SubElement(root, "intro")
        ET.SubElement(intro, "start").text = str(segments["intro"]["start"])
        ET.SubElement(intro, "end").text = str(segments["intro"]["end"])

    if segments.get("outro"):
        outro = ET.SubElement(root, "outro")
        ET.SubElement(outro, "start").text = str(segments["outro"]["start"])
        ET.SubElement(outro, "end").text = str(segments["outro"]["end"])

    xml_bytes = ET.tostring(root, encoding="utf-8", xml_declaration=True)
    return xml_bytes.decode("utf-8")


def main():
    parser = argparse.ArgumentParser(description="Synchronize Re:Zero Perfect Run episodes")
    parser.add_argument("--out-dir", default="", help="Local directory to write files to")
    parser.add_argument("--rclone-target", default="", help="rclone remote path (e.g. cloud_pool_crypt:tv/Re - ZERO.../Season 00)")
    parser.add_argument("--language", default="jp", choices=["jp", "en", "both"], help="Audio language")
    parser.add_argument("--captions", default="cc", choices=["cc", "off", "both"], help="Subtitles mode")
    parser.add_argument("--quality", default="1080p", choices=["1080p", "720p", "480p"], help="Resolution")
    parser.add_argument("--format", default="direct_mp4", choices=["direct_mp4", "hls_m3u8"], help="Stream type")
    parser.add_argument("--season", type=int, default=0, help="Season number (0 = Specials)")
    args = parser.parse_args()

    if not args.out_dir and not args.rclone_target:
        print("Error: Specify either --out-dir or --rclone-target.")
        sys.exit(1)

    print("Fetching arcs metadata from rezeroperfect.run...")
    videos_data = fetch_json(f"{SITE_BASE}/juicy_prototype/videos.json")
    arcs = videos_data.get("arcs", [])
    print(f"Found {len(arcs)} arcs.")

    languages = ["jp", "en"] if args.language == "both" else [args.language]
    captions = ["cc", "off"] if args.captions == "both" else [args.captions]

    global_index = 1
    total_written = 0

    temp_files = []

    for arc in arcs:
        arc_id = arc["id"]
        episodes_data = fetch_json(f"{SITE_BASE}/episodes/{arc_id}.json")
        episodes = episodes_data.get("episodes", [])
        print(f"Arc {arc['number']:02d} ({arc['title']}): {len(episodes)} episodes")

        for ep in episodes:
            ep_title = ep.get("title") or f"Episode {ep['number']}"

            for lang in languages:
                for cap in captions:
                    suffix = ""
                    if len(languages) > 1 or len(captions) > 1:
                        tags = []
                        if len(languages) > 1:
                            tags.append(lang.upper())
                        if len(captions) > 1:
                            tags.append("Subtitles" if cap == "cc" else "Clean")
                        suffix = f" [{ ' '.join(tags) }]"

                    clean_title = sanitize_filename(ep_title)
                    base_name = f"Re ZERO Starting Life in Another World - S{args.season:02d}E{global_index:02d} - [Arc {arc['number']:02d}] {clean_title}{suffix}"

                    stream_url = resolve_stream_url(ep, arc, lang, cap, args.quality, args.format)
                    if not stream_url:
                        continue

                    nfo_content = build_nfo(arc, ep, global_index, args.season, ep_title, suffix)

                    strm_name = f"{base_name}.strm"
                    nfo_name = f"{base_name}.nfo"

                    if args.out_dir:
                        os.makedirs(args.out_dir, exist_ok=True)
                        with open(os.path.join(args.out_dir, strm_name), "w", encoding="utf-8") as f:
                            f.write(stream_url + "\n")
                        with open(os.path.join(args.out_dir, nfo_name), "w", encoding="utf-8") as f:
                            f.write(nfo_content)

                    if args.rclone_target:
                        temp_files.append((strm_name, stream_url + "\n"))
                        temp_files.append((nfo_name, nfo_content))

                    total_written += 1

            global_index += 1

    if args.rclone_target and temp_files:
        print(f"Uploading {len(temp_files)} files to {args.rclone_target} via rclone...")
        import tempfile
        with tempfile.TemporaryDirectory() as td:
            for fname, content in temp_files:
                fpath = os.path.join(td, fname)
                with open(fpath, "w", encoding="utf-8") as f:
                    f.write(content)
            cmd = ["rclone", "copy", td, args.rclone_target, "--verbose"]
            subprocess.run(cmd, check=True)

    print(f"Synchronization complete! Total {total_written} stream files created.")


if __name__ == "__main__":
    main()
