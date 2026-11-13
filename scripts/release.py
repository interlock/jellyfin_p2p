#!/usr/bin/env python3
"""Release helper for the P2P Share Jellyfin plugin.

Subcommands:
  version   <tag>                       Print the 4-part plugin version for a git tag (v0.2.0 -> 0.2.0.0).
  package   --version --dll --out ...   Build the plugin zip (DLL + meta.json) and print its MD5.
  manifest  --version --url --checksum  Add/replace a version in manifest.json (Jellyfin plugin repository).

Plugin metadata (name, guid, description, ...) is read from build.yaml.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
BUILD_YAML = ROOT / "build.yaml"
MANIFEST = ROOT / "manifest.json"

_TAG_RE = re.compile(r"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?$")


def normalize_version(tag: str) -> str:
    """Turn a tag like 'v1.2' or '1.2.3.4' into a 4-part version."""
    match = _TAG_RE.match(tag.strip())
    if not match:
        raise ValueError(f"'{tag}' is not a version tag (expected e.g. v0.2.0 or v0.2.0.1)")
    parts = [int(p) if p is not None else 0 for p in match.groups()]
    return ".".join(str(p) for p in parts)


def load_build(path: Path = BUILD_YAML) -> dict:
    with path.open(encoding="utf-8") as f:
        data = yaml.safe_load(f)
    for key in ("name", "guid", "targetAbi", "overview", "description", "category", "owner", "artifacts"):
        if not data.get(key):
            raise ValueError(f"build.yaml is missing '{key}'")
    data["description"] = " ".join(str(data["description"]).split())
    return data


def utc_timestamp(now: dt.datetime | None = None) -> str:
    now = now or dt.datetime.now(dt.timezone.utc)
    return now.astimezone(dt.timezone.utc).replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")


def build_meta(build: dict, version: str, changelog: str, timestamp: str) -> dict:
    """meta.json written next to the DLL; Jellyfin reads it from the plugin folder."""
    return {
        "category": build["category"],
        "changelog": changelog,
        "description": build["description"],
        "guid": build["guid"],
        "name": build["name"],
        "overview": build["overview"],
        "owner": build["owner"],
        "targetAbi": build["targetAbi"],
        "timestamp": timestamp,
        "version": version,
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "",
        "assemblies": [],
    }


def package(build: dict, version: str, changelog: str, dll_dir: Path, out_dir: Path, timestamp: str) -> tuple[Path, str]:
    out_dir.mkdir(parents=True, exist_ok=True)
    slug = re.sub(r"[^a-z0-9]+", "-", build["name"].lower()).strip("-")
    zip_path = out_dir / f"{slug}_{version}.zip"
    meta = build_meta(build, version, changelog, timestamp)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for artifact in build["artifacts"]:
            source = dll_dir / artifact
            if not source.is_file():
                raise FileNotFoundError(f"artifact not found: {source}")
            z.write(source, artifact)
        z.writestr("meta.json", json.dumps(meta, indent=2) + "\n")
    return zip_path, md5(zip_path)


def md5(path: Path) -> str:
    h = hashlib.md5()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _version_key(v: str) -> tuple[int, ...]:
    return tuple(int(p) for p in v.split("."))


def update_manifest(manifest: list, build: dict, version: str, url: str, checksum: str, changelog: str, timestamp: str) -> list:
    """Return the manifest with this plugin's entry refreshed and `version` added (newest first)."""
    entry = next((p for p in manifest if p.get("guid") == build["guid"]), None)
    if entry is None:
        entry = {"guid": build["guid"], "versions": []}
        manifest.append(entry)

    entry.update(
        {
            "name": build["name"],
            "description": build["description"],
            "overview": build["overview"],
            "owner": build["owner"],
            "category": build["category"],
            "imageUrl": build.get("imageUrl", ""),
        }
    )
    versions = [v for v in entry.get("versions", []) if v.get("version") != version]
    versions.append(
        {
            "version": version,
            "changelog": changelog,
            "targetAbi": build["targetAbi"],
            "sourceUrl": url,
            "checksum": checksum,
            "timestamp": timestamp,
        }
    )
    versions.sort(key=lambda v: _version_key(v["version"]), reverse=True)
    # Keep key order stable and readable.
    ordered = {k: entry[k] for k in ("guid", "name", "description", "overview", "owner", "category", "imageUrl")}
    ordered["versions"] = versions
    manifest[manifest.index(entry)] = ordered
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_version = sub.add_parser("version")
    p_version.add_argument("tag")

    p_package = sub.add_parser("package")
    p_package.add_argument("--version", required=True)
    p_package.add_argument("--changelog", default="")
    p_package.add_argument("--dll-dir", required=True, type=Path)
    p_package.add_argument("--out", required=True, type=Path)
    p_package.add_argument("--timestamp", default=None)

    p_manifest = sub.add_parser("manifest")
    p_manifest.add_argument("--version", required=True)
    p_manifest.add_argument("--url", required=True)
    p_manifest.add_argument("--checksum", required=True)
    p_manifest.add_argument("--changelog", default="")
    p_manifest.add_argument("--timestamp", default=None)
    p_manifest.add_argument("--manifest", type=Path, default=MANIFEST)

    args = parser.parse_args(argv)

    if args.command == "version":
        print(normalize_version(args.tag))
        return 0

    build = load_build()
    timestamp = args.timestamp or utc_timestamp()
    version = normalize_version(args.version)
    changelog = args.changelog.strip() or f"Release {version}"

    if args.command == "package":
        zip_path, checksum = package(build, version, changelog, args.dll_dir, args.out, timestamp)
        print(f"zip={zip_path}")
        print(f"checksum={checksum}")
        print(f"timestamp={timestamp}")
        return 0

    manifest = json.loads(args.manifest.read_text(encoding="utf-8")) if args.manifest.exists() else []
    manifest = update_manifest(manifest, build, version, args.url, args.checksum, changelog, timestamp)
    args.manifest.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest updated: {args.manifest} ({version})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
