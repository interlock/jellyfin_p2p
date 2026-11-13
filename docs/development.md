# P2P Share — Development & Testing

## Prerequisites

* .NET 10 SDK
* Jellyfin 12.0.x for manual testing (two instances, e.g. Docker)

## Layout

```
src/Jellyfin.Plugin.P2PShare/
  Core/            Jellyfin-free logic (tokens, throttling, quotas, policy, sync) — unit tested
  Configuration/   PluginConfiguration + configPage.html/js (embedded resources)
  Api/             RemoteController (peers), ProxyController (local .strm target), AdminController
  Services/        ConfigStore, UsageService/hosted service, CatalogService, PeerStreamService,
                   PeerClientFactory, LibraryRegistrar, RemoteSyncService
  Tasks/           SyncRemoteLibrariesTask
tests/Jellyfin.Plugin.P2PShare.Tests/   xUnit tests for Core
docs/            Design, user guide, API, security, this file
```

Rule of thumb: anything with logic worth testing goes in `Core` and must not reference
Jellyfin or ASP.NET types. `Services`/`Api` translate between Jellyfin and `Core`.

## Build & test

```bash
dotnet build -c Release
dotnet test
```

The Jellyfin packages version is set once in `Directory.Build.props`
(`JellyfinVersion`, currently `12.0.*`) and the ABI in `build.yaml` (`targetAbi`).

CI (`.github/workflows/ci.yml`) runs the release-tooling tests, builds, runs the .NET
tests and uploads a plugin zip as an artifact on every push and pull request.

### What the tests cover

| Area | Tests |
|---|---|
| Tokens | round-trip, whitespace tolerance, truncation/tamper detection, URL validation, credential header parsing, secret hashing |
| Throttling | token bucket debt/refill/rate changes with a manual clock, throttled copy timing, chunk sizing, cancellation, live rate updates |
| Quotas | hourly/daily/weekly/monthly windows (UTC, year end, Monday start), rollover, interval change, concurrency, persistence, corrupt file |
| Sharing | authorization matrix (disabled, expired, wrong secret, unknown share, CIDR allowlist incl. IPv4-mapped), stream policy matrix, quota meter, concurrency registry (incl. races), HTTP ranges, ffmpeg arguments |
| Sync | mirror layout for movies/series/episodes/music/music videos, NFO validity, collisions, hostile names, sanitizer, manifest diffing, safe deletes, path traversal, paging, library merge, failure handling, HTTP client headers/errors |
| Validation | share and settings validation and normalization |

## Manual end-to-end test plan

Use two Jellyfin 12 containers on one Docker network:

```bash
docker network create p2p
docker run -d --name alice --network p2p -p 8096:8096 -v $PWD/alice:/config -v /path/to/media:/media jellyfin/jellyfin:12.0
docker run -d --name bob   --network p2p -p 8097:8096 -v $PWD/bob:/config jellyfin/jellyfin:12.0
# install from the plugin repository, or extract a CI/release zip into ./alice/plugins/P2P Share_<version>/ and ./bob/plugins/..., restart both
```

1. **Alice**: set Public server URL to `http://alice:8096`, create a share for a movie
   library and a TV library, copy the token.
2. **Bob**: Incoming connections → paste → Check token (libraries and limits shown) → Add.
3. Wait for the sync (or run the scheduled task). Bob gets two new libraries with
   posters; series are grouped into seasons.
4. Play a movie on Bob; seek forward/back (range requests). Check Alice's **Activity**.
5. Set the share's bandwidth to 2 Mbps → playback of a high-bitrate file buffers;
   Activity shows the slower byte count.
6. Set a quota of 0.05 GB → playback stops after ~50 MB; new playback returns 429.
7. Enable transcoding and a 4 Mbps cap → a high-bitrate file now plays transcoded.
8. Set Max concurrent streams = 1 → a second simultaneous stream is refused.
9. Add an IP allowlist that excludes Bob → Bob's sync status shows "not allowed".
10. **New token** → Bob's sync fails with "Invalid share credentials"; re-add with the new token.
11. Untick a library on Bob → its library disappears. Revoke the share on Alice → Bob's
    next sync reports access denied.
12. Remove the connection on Bob → libraries and mirror folders are gone.

## Releasing

See [README → Releasing](../README.md#releasing-maintainers). In short: push a tag
`vX.Y.Z` (annotated tag message = changelog) or run the *Release* workflow manually.
The workflow tests, builds, publishes `p2p-share_X.Y.Z.0.zip` to a GitHub release and
adds the version to `manifest.json` on `main`.

Release tooling lives in `scripts/release.py` (tested by `scripts/test_release.py`):

```bash
python3 scripts/release.py version v0.2.0                       # -> 0.2.0.0
python3 scripts/release.py package --version 0.2.0 --dll-dir <bin dir> --out dist
python3 scripts/release.py manifest --version 0.2.0 --url <zip url> --checksum <md5>
```

When targeting a new Jellyfin major version, update `JellyfinVersion` in
`Directory.Build.props`, `targetAbi`/`framework` in `build.yaml`, and the target
framework in both `.csproj` files.

## Coding conventions

* Nullable reference types on; no implicit usings.
* All I/O async with `ConfigureAwait(false)` outside controllers.
* Never mutate `ConfigStore.Snapshot`; use `ConfigStore.Update`.
* Peer wire format changes must stay backwards compatible within protocol version 1.
