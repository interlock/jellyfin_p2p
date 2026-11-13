"""Tests for scripts/release.py. Run: python3 -m unittest discover -s scripts"""

import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import release  # noqa: E402

BUILD = {
    "name": "P2P Share",
    "guid": "68f74e62-ee9d-4d89-b9e4-7e41baf0235d",
    "targetAbi": "12.0.0.0",
    "overview": "Share libraries",
    "description": "Long description",
    "category": "General",
    "owner": "interlock",
    "artifacts": ["Jellyfin.Plugin.P2PShare.dll"],
}


class VersionTests(unittest.TestCase):
    def test_normalizes_tags(self):
        self.assertEqual(release.normalize_version("v0.2.0"), "0.2.0.0")
        self.assertEqual(release.normalize_version("v1"), "1.0.0.0")
        self.assertEqual(release.normalize_version("1.2.3.4"), "1.2.3.4")
        self.assertEqual(release.normalize_version(" v10.0.1 "), "10.0.1.0")

    def test_rejects_non_versions(self):
        for bad in ("main", "v1.2-beta", "v1.2.3.4.5", ""):
            with self.assertRaises(ValueError):
                release.normalize_version(bad)


class BuildYamlTests(unittest.TestCase):
    def test_repo_build_yaml_is_complete(self):
        build = release.load_build()
        self.assertEqual(build["guid"], BUILD["guid"])
        self.assertTrue(build["targetAbi"].startswith("12."))
        self.assertNotIn("\n", build["description"])


class PackageTests(unittest.TestCase):
    def test_zip_contains_dll_and_meta(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            (tmp / "Jellyfin.Plugin.P2PShare.dll").write_bytes(b"MZ fake dll")
            zip_path, checksum = release.package(BUILD, "0.2.0.0", "Fixes", tmp, tmp / "out", "2026-11-13T00:00:00Z")

            self.assertEqual(zip_path.name, "p2p-share_0.2.0.0.zip")
            self.assertEqual(len(checksum), 32)
            self.assertEqual(checksum, release.md5(zip_path))
            with zipfile.ZipFile(zip_path) as z:
                self.assertEqual(sorted(z.namelist()), ["Jellyfin.Plugin.P2PShare.dll", "meta.json"])
                meta = json.loads(z.read("meta.json"))
            self.assertEqual(meta["version"], "0.2.0.0")
            self.assertEqual(meta["guid"], BUILD["guid"])
            self.assertEqual(meta["targetAbi"], "12.0.0.0")
            self.assertEqual(meta["changelog"], "Fixes")

    def test_missing_artifact_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(FileNotFoundError):
                release.package(BUILD, "0.2.0.0", "", Path(tmp), Path(tmp) / "out", "t")


class ManifestTests(unittest.TestCase):
    def add(self, manifest, version):
        return release.update_manifest(manifest, BUILD, version, f"https://x/{version}.zip", "a" * 32, "log", "2026-11-13T00:00:00Z")

    def test_creates_entry_and_orders_versions_newest_first(self):
        manifest = self.add([], "0.1.0.0")
        manifest = self.add(manifest, "0.10.0.0")
        manifest = self.add(manifest, "0.2.0.0")

        self.assertEqual(len(manifest), 1)
        entry = manifest[0]
        self.assertEqual(entry["guid"], BUILD["guid"])
        self.assertEqual([v["version"] for v in entry["versions"]], ["0.10.0.0", "0.2.0.0", "0.1.0.0"])
        self.assertEqual(entry["versions"][0]["targetAbi"], "12.0.0.0")

    def test_re_release_replaces_version(self):
        manifest = self.add([], "0.1.0.0")
        manifest = release.update_manifest(manifest, BUILD, "0.1.0.0", "https://new", "b" * 32, "log", "t")
        versions = manifest[0]["versions"]
        self.assertEqual(len(versions), 1)
        self.assertEqual(versions[0]["sourceUrl"], "https://new")

    def test_keeps_other_plugins(self):
        other = {"guid": "other", "name": "Other", "versions": []}
        manifest = self.add([other], "0.1.0.0")
        self.assertEqual(len(manifest), 2)
        self.assertIs(manifest[0], other)

    def test_repo_manifest_is_valid(self):
        manifest = json.loads(release.MANIFEST.read_text(encoding="utf-8"))
        self.assertIsInstance(manifest, list)
        entry = next(p for p in manifest if p["guid"] == BUILD["guid"])
        for key in ("name", "description", "overview", "owner", "category", "versions"):
            self.assertIn(key, entry)
        for v in entry["versions"]:
            for key in ("version", "changelog", "targetAbi", "sourceUrl", "checksum", "timestamp"):
                self.assertIn(key, v)


if __name__ == "__main__":
    unittest.main()
