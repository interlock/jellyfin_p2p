# P2P Share — Design Document

Status: v0.1 (implemented) · Target: Jellyfin 12.0.x (.NET 10) · Plugin GUID `68f74e62-ee9d-4d89-b9e4-7e41baf0235d`

## 1. Goals

P2P Share lets the operator of one Jellyfin server ("the **sharing** server") give the
operator of another Jellyfin server ("the **receiving** server") access to selected
libraries. Both servers run this plugin. There is no central service: servers talk
directly to each other over HTTP(S).

Functional goals:

1. The sharing operator creates a **share** and gets an **invite token** — a single
   copy-pasteable string that carries the server address and the credentials.
2. Each share has its own policy: which libraries, whether transcoding is allowed,
   a maximum streaming bitrate, a maximum bandwidth (throughput), a data volume quota per
   interval, a concurrent-stream limit, an expiry date, and an optional IP allowlist.
3. Global settings bound the server as a whole: total bandwidth, total data volume per
   interval, total concurrent remote streams and transcodes.
4. The receiving operator pastes the token; each shared library then appears as a
   normal Jellyfin library on the receiving server, browsable and playable from any
   Jellyfin client.
5. Shares can be revoked or their secret rotated at any time.

Non-goals (v1):

* NAT traversal / relays. The sharing server must be reachable from the receiving
  server (port forward, reverse proxy, VPN, Tailscale, etc.).
* Syncing user watch state between servers.
* Re-sharing a library that was itself received from a peer (blocked on purpose).
* Live TV, books, photos and playlists.

## 2. Terminology

| Term | Meaning |
|---|---|
| Share | An outgoing grant configured on the sharing server. Identified by a GUID. |
| Invite token | The string given to the receiving operator: `jfp2p1.<payload>.<checksum>` |
| Share secret | 256-bit random value inside the token. Only its SHA-256 hash is stored. |
| Connection | An incoming grant configured on the receiving server (one per pasted token). |
| Mirror library | The local Jellyfin library created on the receiving server for one shared library. |
| Proxy key | A random per-connection secret embedded in local `.strm` URLs on the receiving server. |

## 3. Architecture overview

```
 Receiving server (B)                                   Sharing server (A)
 ┌─────────────────────────────────┐                   ┌──────────────────────────────────┐
 │ Jellyfin clients                │                   │  P2P Share plugin                 │
 │   │ play "Movie (2001)"         │                   │                                   │
 │   ▼                             │                   │  /P2PShare/Remote/*  (token auth) │
 │ Jellyfin core                   │                   │    Info, Libraries, Items,        │
 │   │ reads Movie.strm →          │   HTTPS + header  │    Images, Stream                 │
 │   │ http://127.0.0.1:8096/      │   X-P2PShare-Key  │        │                          │
 │   │   P2PShare/Proxy/{c}/{i}?k= ├──────────────────►│  ShareAuthorizer → StreamPolicy   │
 │   ▼                             │                   │  → ActiveStreamRegistry           │
 │ P2P Share plugin (proxy)        │◄──────────────────┤  → ThrottledCopier (token buckets)│
 │   sync task writes .strm/.nfo/  │   bytes (ranged)  │  → UsageLedger (quotas)           │
 │   artwork into mirror folders   │                   │  → file or ffmpeg transcode       │
 └─────────────────────────────────┘                   └──────────────────────────────────┘
```

The plugin is symmetric: every installation can both share and receive.

### 3.1 Code layout

```
src/Jellyfin.Plugin.P2PShare/
  Core/            Pure .NET logic, no Jellyfin references (unit tested)
    Tokens/        Invite token codec, secret generation and hashing
    Throttling/    Token buckets, bandwidth manager, throttled copy
    Quotas/        Quota windows, usage ledger and persistence
    Sharing/       Share authorization, stream policy, active stream registry, HTTP range parsing, ffmpeg args
    Sync/          Remote DTOs, remote client, mirror planner, NFO writer, mirror synchronizer
    Model/         Settings POCOs persisted in the plugin configuration
  Configuration/   PluginConfiguration + admin web page (HTML/JS, embedded)
  Api/             ASP.NET controllers: Remote (peer-facing), Proxy (local), Admin
  Services/        Jellyfin-facing services: catalog mapper, library registrar, config store, hosted services
  Tasks/           Scheduled task: sync remote libraries
tests/Jellyfin.Plugin.P2PShare.Tests/   xUnit tests for Core
```

Keeping `Core` free of Jellyfin types makes the important behaviour (security,
limits, sync) testable without a running server.

## 4. Invite tokens and authentication

### 4.1 Token format

```
jfp2p1.<base64url(JSON payload)>.<checksum>
```

Payload (short keys keep the token compact):

```json
{ "v": 1, "u": "https://media.example.com", "n": "Alice's Jellyfin",
  "sid": "<server id>", "i": "<share GUID>", "s": "<base64url 32-byte secret>" }
```

`checksum` is the first 8 characters of `base64url(SHA-256(payload segment))`. It
catches truncated or mangled copy/paste before any network call. It is **not** a
signature; authenticity comes from the secret.

### 4.2 Secret storage

* The secret is generated with `RandomNumberGenerator` (32 bytes).
* The sharing server stores only `SHA-256(secret)` (hex). A high-entropy random secret
  does not need a slow KDF.
* The full token is shown once when created or rotated. Losing it means rotating.
* Verification uses `CryptographicOperations.FixedTimeEquals`.

### 4.3 Request authentication

The receiving server sends `X-P2PShare-Key: <shareId>.<secret>` on every peer request.
A custom header is used so Jellyfin's own `Authorization` handling never sees it.
The sharing server's `ShareAuthorizer` rejects requests when:

| Check | Result |
|---|---|
| Sharing globally disabled | 503 |
| Header missing / malformed | 401 |
| Unknown share id or wrong secret | 401 (same response, no oracle) |
| Share disabled | 403 |
| Share expired | 403 |
| Client IP not in the share's allowlist (if set) | 403 |

On the receiving server the token (including the secret) is stored in the plugin
configuration, which is only readable by Jellyfin administrators.

### 4.4 Transport security

Tokens are bearer credentials. HTTPS is strongly recommended; the global
`RequireHttps` setting refuses to create tokens with an `http://` URL and refuses to
connect to `http://` peers. It is off by default so LAN/VPN setups work out of the box,
and the admin page shows a warning when it is off.

## 5. Policies and limits

### 5.1 Global settings (sharing side)

| Setting | Default | Meaning |
|---|---|---|
| `SharingEnabled` | true | Master switch for answering peer requests |
| `PublicServerUrl` | "" | Base URL peers use to reach this server; embedded in tokens |
| `ServerDisplayName` | server name | Shown to peers |
| `RequireHttps` | false | Refuse plain-HTTP tokens/peers |
| `GlobalMaxBandwidthMbps` | 0 (∞) | Total outbound throughput to all peers |
| `GlobalQuotaGB` / `GlobalQuotaInterval` | 0 (∞) / Monthly | Total data volume per interval |
| `GlobalMaxConcurrentStreams` | 0 (∞) | Concurrent peer streams |
| `AllowTranscoding` | true | Master switch; a share's flag cannot override it |
| `MaxConcurrentTranscodes` | 2 | Concurrent transcodes for peers |
| `DefaultTokenLifetimeDays` | 0 (never) | Default expiry for new shares |

### 5.2 Global settings (receiving side)

| Setting | Default | Meaning |
|---|---|---|
| `LocalProxyBaseUrl` | `http://127.0.0.1:8096` | Base of the proxy URL written in `.strm` files. Must be reachable by this server's ffmpeg and by any client that direct-plays the `.strm` URL itself (then use a LAN/public address) |
| `SyncIntervalHours` | 6 | Default trigger of the sync scheduled task |
| `DownloadArtwork` | true | Download posters/backdrops into mirror folders |
| `LibraryNameTemplate` | `{library} ({server})` | Name of mirror libraries |
| `MirrorRootPath` | `<plugin data>/mirrors` | Where `.strm` trees are written |

### 5.3 Per-share settings

| Setting | Default | Meaning |
|---|---|---|
| `Name` | — | Label, e.g. who it is for |
| `LibraryIds` | — | Local libraries included in the share |
| `Enabled` | true | Temporarily disable without revoking |
| `ExpiresAt` | none | Hard expiry |
| `AllowTranscoding` | false | Allow server-side transcoding for this share |
| `MaxStreamingBitrateMbps` | 0 (∞) | Media bitrate cap; higher-bitrate items are transcoded down (if allowed) or refused |
| `MaxBandwidthMbps` | 0 (∞) | Throughput cap across all of this share's streams |
| `QuotaGB` / `QuotaInterval` | 0 (∞) / Monthly | Data volume per interval |
| `MaxConcurrentStreams` | 2 | Concurrent streams |
| `AllowedNetworks` | empty (any) | CIDR allowlist, e.g. `203.0.113.0/24` |
| `Notes` | "" | Free text |

### 5.4 Per-connection settings (receiving side)

| Setting | Default | Meaning |
|---|---|---|
| `Token` | — | The pasted invite |
| `Enabled` | true | Stops sync and proxying when off |
| `Libraries[]` | from peer | Per remote library: `Enabled`, `LocalName` |
| `PreferTranscode` | false | Ask the peer to transcode (if the share allows it) |
| `PreferredMaxBitrateMbps` | 0 | Target bitrate when transcoding |

### 5.5 Bandwidth limiting

Throughput is limited with **token buckets** measured in bytes/second, using a
"debt" model: a reader reserves `n` bytes, the bucket balance may go negative, and the
reader sleeps until the balance is non-negative again. Each stream passes through the
**global** bucket and the **share's** bucket; the effective delay is the larger of the
two. Concurrent streams on the same bucket share it fairly because each reservation
adds to the same debt. Bucket capacity (burst) is one second of traffic. Rate changes
made in the admin UI apply immediately to running streams.

Chunk size adapts to the rate (≈1/10 s of traffic, between 4 KiB and 64 KiB) to keep
playback smooth at low limits.

### 5.6 Volume quotas

The `UsageLedger` tracks bytes per key (`share:<id>` and `global`) in calendar windows
(UTC): **Hourly**, **Daily**, **Weekly** (Monday 00:00), **Monthly** (1st 00:00). When a
window ends the counter resets; the previous window's total is retained for display,
and lifetime totals are kept. The ledger is persisted to
`<plugin data>/usage.json` every minute and at shutdown.

Enforcement:

* Before a stream starts, the request is refused with **429** if the share or global
  quota is already exhausted.
* While streaming, every chunk is metered; when either quota is reached the stream is
  ended. A stream can therefore overshoot by at most one chunk.
* Artwork downloads count against quotas but are not throttled.

### 5.7 Stream policy

`StreamPolicy.Decide` takes the request (direct or transcode, requested bitrate), the
item's bitrate, the share and global settings and returns *Direct*, *Transcode(bitrate)*
or *Deny(reason, status)*:

1. Quota exhausted (share or global) → 429.
2. Transcoding is effective only if both global and share allow it.
3. Transcode requested: denied (403) if not effective; target bitrate is the requested
   bitrate, capped by `MaxStreamingBitrateMbps`, defaulting to 8 Mbps.
4. Direct requested: if the item exceeds `MaxStreamingBitrateMbps`, transcode at the cap
   when effective, otherwise deny (403).

Concurrency is enforced separately and atomically by `ActiveStreamRegistry`
(share streams, global streams, global transcodes) → 429 when full.

### 5.8 Transcoding

When transcoding, the sharing server launches Jellyfin's own ffmpeg
(`IMediaEncoder.EncoderPath`) producing H.264 + AAC in MPEG-TS to stdout, which is
streamed through the same throttling and metering path. Seeking is supported by the
`startTimeTicks` parameter (`-ss`); transcoded responses do not support HTTP ranges.
Direct streams support single HTTP byte ranges (`206 Partial Content`).

## 6. Receiving side: mirror libraries

### 6.1 Why `.strm` files

Jellyfin libraries are built from the file system. The most robust way to make remote
content look local, without patching Jellyfin, is to write a folder tree of `.strm`
files (each containing one URL) plus `.nfo` metadata and artwork, and register that
folder as a library. Jellyfin then scans, displays and plays it like any other
library, and all clients work unchanged.

### 6.2 Proxy URLs

`.strm` files never contain the share secret. They point at the receiving server's own
proxy endpoint:

```
http://127.0.0.1:8096/P2PShare/Proxy/{connectionId}/{remoteItemId}?key={proxyKey}
```

Some Jellyfin clients open HTTP-backed (`.strm`) media sources directly instead of
through the server, so `LocalProxyBaseUrl` is configurable (see §5.2).

The proxy validates the per-connection `proxyKey` (random, rotated whenever the
connection is re-created), adds the `X-P2PShare-Key` header and streams the response
from the peer, forwarding `Range`, `Content-Range`, `Content-Length` and
`Content-Type`. If the connection prefers transcoding, the query includes
`transcode=true&maxBitrate=…`.

### 6.3 Folder layout

Paths follow Jellyfin naming conventions so Jellyfin's own resolvers identify items:

| Remote type | Path |
|---|---|
| Movie | `Title (Year) [tmdbid-123]/Title (Year).strm` + `Title (Year).nfo`, `poster.jpg`, `fanart.jpg` |
| Series | `Series (Year) [tvdbid-123]/tvshow.nfo`, `poster.jpg`, `fanart.jpg` |
| Episode | `Series (Year) [tvdbid-123]/Season 01/Series S01E02 - Name.strm` + `.nfo` + `-thumb.jpg` |
| Music video | `Artist - Title.strm` + `.nfo` |
| Audio | `Album Artist/Album/01 - Title.strm`, `folder.jpg` |
| Other video | `Title.strm` + `.nfo` |

File names are sanitized for all platforms. Two items that map to the same path get
` [<first 8 chars of id>]` appended, deterministically.

### 6.4 Sync algorithm

The `SyncRemoteLibrariesTask` scheduled task (default every 6 h, also "Sync now" in the
UI) runs `RemoteSyncService` → `ConnectionSynchronizer`, which does, per enabled
connection and library:

1. `GET /Remote/Libraries` — refresh the list; new libraries are added (enabled by
   default), libraries no longer shared are disabled and their mirrors removed.
2. Page through `GET /Remote/Libraries/{id}/Items` (500 per page).
3. `MirrorPlanner` + `NfoWriter` build the **desired file set**: relative path → content
   (or image reference) + fingerprint.
4. `MirrorSynchronizer.Diff` compares desired files against the folder's manifest
   (`.p2pshare-manifest.json`) → create/update/delete actions.
5. `MirrorSynchronizer.SyncAsync` applies actions atomically per file (write temp + rename), downloads
   artwork, deletes stale files and prunes empty directories. Only files listed in the
   manifest are ever deleted.
6. `LibraryRegistrar` ensures a Jellyfin library exists for the mirror folder (with
   chapter-image, trickplay and real-time monitoring extraction disabled so the
   scan does not download whole files), and queues a library scan if anything changed.

Removing a connection removes its libraries and mirror folders.

## 7. Peer API (sharing side)

All endpoints are `[AllowAnonymous]` at the Jellyfin level and authenticated by
`X-P2PShare-Key`. Full reference: [api.md](api.md).

| Method & path | Purpose |
|---|---|
| `GET /P2PShare/Remote/Info` | Server identity, plugin version, share policy and usage |
| `GET /P2PShare/Remote/Libraries` | Libraries in the share |
| `GET /P2PShare/Remote/Libraries/{id}/Items?startIndex&limit` | Paged catalog |
| `GET /P2PShare/Remote/Items/{id}/Images/{type}` | Primary / Backdrop / Thumb artwork |
| `GET /P2PShare/Remote/Items/{id}/Stream?transcode&maxBitrate&startTimeTicks` | Media |

Every item access verifies that the item belongs to one of the share's libraries.
Items that are themselves `.strm`-backed (e.g. received from another peer) are never
served.

## 8. Admin UI

A plugin configuration page (Dashboard → P2P Share, also under Plugins) with four tabs:

* **Settings** — global settings (§5.1, §5.2).
* **Outgoing shares** — create/edit/disable/rotate/delete shares, per-share usage, copy token.
* **Incoming connections** — paste a token (previewed before saving), choose which
  libraries to mirror and their names, sync now, remove.
* **Activity** — global usage for the current period and every running peer stream.

It talks to `/P2PShare/Admin/*` endpoints (administrator only).

## 9. Persistence

| Data | Location |
|---|---|
| Settings, shares (hashed secrets), connections (tokens, proxy keys) | Jellyfin plugin XML config |
| Usage counters | `<plugin data>/usage.json` |
| Mirror trees + manifests | `<MirrorRootPath>/<connectionId>/<remoteLibraryId>/` |

Config mutations go through `ConfigStore`, which serializes updates with a lock so the
admin API and the sync task never overwrite each other.

## 10. Failure handling

* Peer unreachable during sync → connection status set to the error, mirror left
  unchanged (no mass deletion on transient failure).
* Peer returns 401/403 during sync → status "Access denied by peer: …", mirror left unchanged
  until the admin removes the connection.
* Quota/concurrency refusal during playback → the peer's 429 is passed through; the
  Jellyfin client shows a playback error.
* Client disconnect → the copy loop is cancelled, the ffmpeg process (if any) is
  killed and the stream lease released.

## 11. Testing strategy

* `Core` is covered by xUnit tests (`tests/`): token round-trip and tamper detection,
  secret hashing, token bucket timing with a manual clock, throttled copy rates,
  quota windows across boundaries, ledger persistence, authorization matrix, stream
  policy matrix, registry concurrency, range parsing, ffmpeg arguments, path planning
  and sanitization, NFO output, sync diffing and execution on temp directories, and the
  remote client against a fake `HttpMessageHandler`.
* Jellyfin-facing glue (controllers, library registration) is thin and verified by
  CI compilation plus the manual test plan in [development.md](development.md).

## 12. Future work

* Watch-state sync between peers; per-user visibility of mirror libraries.
* Relay/NAT traversal option.
* Subtitle passthrough for external subtitle files.
* Push notifications from sharer to receiver on library changes (instead of polling).
* Optional Jellyfin 10.11 (.NET 9) back-port build.
