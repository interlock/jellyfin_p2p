# Jellyfin P2P Share

[![CI](https://github.com/interlock/jellyfin_p2p/actions/workflows/ci.yml/badge.svg)](https://github.com/interlock/jellyfin_p2p/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/interlock/jellyfin_p2p)](https://github.com/interlock/jellyfin_p2p/releases)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)

A Jellyfin plugin that lets server operators share libraries with each other.

The sharing operator creates an **invite token** per peer and decides, per token, which
libraries are shared, whether transcoding is allowed, the maximum bitrate, the maximum
bandwidth and the data volume allowed per hour/day/week/month. Global limits cap the
server as a whole. The receiving operator pastes the token and the shared libraries
appear as normal libraries on their server.

* [User guide](docs/user-guide.md)
* [Design document](docs/design.md)
* [Peer & admin API](docs/api.md)
* [Security model](docs/security.md)
* [Development & testing](docs/development.md)

## Requirements

* Jellyfin **12.0 or newer** on both servers
* The sharing server must be reachable from the receiving server (HTTPS recommended)

## Installation

### From the plugin repository (recommended)

1. In Jellyfin, open **Dashboard → Plugins → Manage Repositories** (the *Repositories*
   tab on some versions) and add a repository:
   * **Name:** `P2P Share`
   * **URL:**
     ```
     https://raw.githubusercontent.com/interlock/jellyfin_p2p/main/manifest.json
     ```
2. Go to **Dashboard → Plugins → Catalog**, find **P2P Share** (category *General*) and
   click **Install**.
3. Restart Jellyfin when prompted.
4. Open **Dashboard → P2P Share** to configure it — see the [user guide](docs/user-guide.md).

Updates appear in the catalog automatically when a new version is released; Jellyfin
installs them on restart if automatic plugin updates are enabled.

### Manual installation

1. Download `p2p-share_<version>.zip` from the
   [releases page](https://github.com/interlock/jellyfin_p2p/releases).
2. Extract it into a new folder `<jellyfin data>/plugins/P2P Share_<version>/`
   (it contains `Jellyfin.Plugin.P2PShare.dll` and `meta.json`).
   * Docker: `/config/plugins/`
   * Debian/Ubuntu: `/var/lib/jellyfin/plugins/`
   * Windows: `%LOCALAPPDATA%\jellyfin\plugins\`
3. Restart Jellyfin.

## How it works (short)

* **Sharing server**: exposes a token-authenticated peer API (`/P2PShare/Remote/*`) that
  lists the shared libraries and streams items — directly with HTTP ranges or transcoded
  via Jellyfin's ffmpeg — through token-bucket bandwidth limits, data quotas and
  concurrency limits.
* **Receiving server**: a scheduled task mirrors each shared library as a folder of
  `.strm` + `.nfo` + artwork files and registers it as a normal Jellyfin library. The
  `.strm` URLs point at a local proxy endpoint, so the share secret never leaves the
  plugin configuration.

## Build from source

```bash
dotnet build -c Release
dotnet test
python3 -m unittest discover -s scripts   # release tooling (needs PyYAML)
```

The plugin DLL is at `src/Jellyfin.Plugin.P2PShare/bin/Release/net10.0/Jellyfin.Plugin.P2PShare.dll`.

## Releasing (maintainers)

Releases are fully automated by [`.github/workflows/release.yml`](.github/workflows/release.yml).
Either push a version tag:

```bash
git tag -a v0.2.0 -m "Short changelog shown in the Jellyfin catalog"
git push origin v0.2.0
```

or run **Actions → Release → Run workflow** with a version (e.g. `0.2.0`) and an
optional changelog. Tick **dry run** to build and package without publishing.

The workflow then:

1. converts the tag to a 4-part plugin version (`v0.2.0` → `0.2.0.0`),
2. runs all tests and builds the plugin stamped with that version,
3. packages `p2p-share_0.2.0.0.zip` (DLL + `meta.json`) and publishes a GitHub release,
4. adds the version (download URL, MD5 checksum, `targetAbi`, changelog) to
   [`manifest.json`](manifest.json) on `main`, which is what Jellyfin servers read.

Plugin metadata (name, GUID, description, category, `targetAbi`) lives in
[`build.yaml`](build.yaml). If `main` is branch-protected, allow GitHub Actions to push
to it so the manifest can be updated.

## License

[GPL-3.0](LICENSE). Jellyfin plugins link against GPL-licensed Jellyfin assemblies.
