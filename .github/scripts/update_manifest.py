#!/usr/bin/env python3
"""
Updates Jellyfin plugin manifest.json (and optionally meta.json) with release details,
MD5 checksum, tag download URL, and changelog.
"""

import argparse
import datetime
import hashlib
import json
import os
import sys

PLUGIN_GUID = "aaf63d7c-95e2-4cda-a6fc-7fbea2b90f8a"


def compute_md5(file_path: str) -> str:
    hash_md5 = hashlib.md5()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            hash_md5.update(chunk)
    return hash_md5.hexdigest()


def update_meta_file(meta_path: str, version: str, changelog: str, now_iso: str) -> None:
    if not os.path.exists(meta_path):
        print(f"Error: {meta_path} not found", file=sys.stderr)
        sys.exit(1)

    with open(meta_path, "r", encoding="utf-8") as f:
        meta = json.load(f)

    meta["version"] = version
    meta["timestamp"] = now_iso
    if changelog:
        meta["changelog"] = changelog

    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Successfully updated {meta_path} for version {version}")


def main():
    parser = argparse.ArgumentParser(description="Update Jellyfin manifest.json and meta.json")
    parser.add_argument("--manifest-path", default="manifest.json", help="Path to manifest.json")
    parser.add_argument("--zip-path", default="", help="Path to compiled animekai.zip")
    parser.add_argument("--version", required=True, help="4-part assembly version (e.g. 0.1.0.0)")
    parser.add_argument("--tag", default="", help="Release git tag (defaults to v<version>)")
    parser.add_argument("--changelog-file", help="Path to markdown/text changelog summary")
    parser.add_argument("--repo", default="FaserF/animekai-jellyfin", help="GitHub repo (owner/repo)")
    parser.add_argument("--update-meta", default="", help="Optional path to meta.json to update version/timestamp/changelog only")
    args = parser.parse_args()

    changelog = ""
    if args.changelog_file and os.path.exists(args.changelog_file):
        with open(args.changelog_file, "r", encoding="utf-8") as f:
            changelog = f.read().strip()
    if not changelog:
        changelog = f"Release {args.version}"

    now_iso = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    if args.update_meta:
        update_meta_file(args.update_meta, args.version, changelog, now_iso)
        if not args.zip_path:
            return

    if not args.zip_path or not os.path.exists(args.zip_path):
        print(f"Error: {args.zip_path} not found", file=sys.stderr)
        sys.exit(1)

    checksum = compute_md5(args.zip_path)
    print(f"Calculated MD5 for {args.zip_path}: {checksum}")

    tag = args.tag.strip() or f"v{args.version}"
    source_url = f"https://github.com/{args.repo}/releases/download/{tag}/animekai.zip"

    with open(args.manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    target_abis = ["12.0.0.0", "10.10.0.0"]

    for plugin in manifest:
        if plugin.get("guid", "").lower() == PLUGIN_GUID:
            existing_versions = plugin.get("versions", [])
            updated_versions = [v for v in existing_versions if v.get("version") != args.version]

            for abi in target_abis:
                updated_versions.insert(0, {
                    "version": args.version,
                    "changelog": changelog,
                    "targetAbi": abi,
                    "sourceUrl": source_url,
                    "checksum": checksum,
                    "timestamp": now_iso,
                })

            plugin["versions"] = updated_versions

    with open(args.manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Successfully updated {args.manifest_path} for version {args.version} (tag={tag})")


if __name__ == "__main__":
    main()
