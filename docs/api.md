# P2P Share — HTTP API Reference

All responses are JSON (camelCase, enums as strings) unless noted. Errors use
`{ "error": "<message>" }` with an appropriate status code.

## Peer API (sharing server)

Called by the receiving server. Not authenticated by Jellyfin users; every request must
carry the share credential:

```
X-P2PShare-Key: <shareId as 32 hex chars>.<secret>
X-P2PShare-Protocol: 1        (informational)
```

Common failures for every endpoint:

| Status | Meaning |
|---|---|
| 401 | Missing, malformed or invalid credential (unknown share and wrong secret are indistinguishable) |
| 403 | Share disabled, expired, or request IP not in the share's allowlist |
| 503 | Sharing disabled on this server |

### `GET /P2PShare/Remote/Info`

```json
{
  "protocolVersion": 1,
  "serverName": "Alice's Jellyfin",
  "serverId": "6f1c…",
  "pluginVersion": "0.1.0.0",
  "share": {
    "name": "Bob",
    "allowTranscoding": true,
    "maxStreamingBitrateMbps": 20,
    "maxBandwidthMbps": 50,
    "quotaGB": 200,
    "quotaInterval": "Monthly",
    "quotaUsedBytes": 1234567890,
    "quotaResetsAt": "2026-12-01T00:00:00+00:00",
    "maxConcurrentStreams": 2,
    "expiresAt": null
  }
}
```

`allowTranscoding` reflects both the share and the global switch.

### `GET /P2PShare/Remote/Libraries`

```json
[ { "id": "f137a2dd21bbc1b99aa5c0f6bf02a805", "name": "Movies", "collectionType": "movies", "itemCount": 812 } ]
```

`itemCount` counts playable items (movies, episodes, tracks, videos).

### `GET /P2PShare/Remote/Libraries/{libraryId}/Items?startIndex=0&limit=500`

`limit` is clamped to 1–1000. Items are ordered by sort name.

```json
{
  "startIndex": 0,
  "totalRecordCount": 1203,
  "items": [
    {
      "id": "…", "type": "Episode", "name": "Pilot",
      "seriesId": "…", "seriesName": "The Wire",
      "parentIndexNumber": 1, "indexNumber": 1,
      "productionYear": 2002, "premiereDate": "2002-06-02T00:00:00Z",
      "overview": "…", "genres": ["Drama"], "studios": ["HBO"],
      "runTimeTicks": 36000000000, "bitrate": 4500000, "container": "mkv", "size": 1900000000,
      "providerIds": { "Tvdb": "349232", "Imdb": "tt0749451" },
      "images": [ { "type": "Primary", "tag": "8dd2c3…", "extension": ".jpg" } ]
    }
  ]
}
```

`type` is one of `Movie`, `Series`, `Episode`, `Audio`, `MusicAlbum`, `MusicVideo`,
`Video`. Optional fields are omitted when empty. `404` if the library is not in the share.

### `GET /P2PShare/Remote/Items/{itemId}/Images/{type}`

`type`: `Primary`, `Backdrop` or `Thumb`. Returns the image file. Counts against data
quotas (not bandwidth limits). `404` if the item is not in the share or has no such image.

### `GET|HEAD /P2PShare/Remote/Items/{itemId}/Stream`

| Query | Default | Meaning |
|---|---|---|
| `transcode` | `false` | Ask for a server-side transcode |
| `maxBitrate` | `0` | Target total bitrate (bits/s) for a transcode |
| `startTimeTicks` | `0` | Start offset for a transcode (100 ns ticks) |

**Direct** responses support a single `Range: bytes=…` header (`200`, `206` with
`Content-Range`, or `416`), `Accept-Ranges: bytes`, and the file's MIME type.

**Transcoded** responses are `video/mp2t` (H.264 + AAC) or `audio/aac`, without ranges.

Additional failures:

| Status | Meaning |
|---|---|
| 403 | Transcoding requested but not allowed; or item bitrate above the share's cap and transcoding not allowed |
| 404 | Item not in the share, or not streamable (e.g. a `.strm` item) |
| 429 | Share or global data quota used up; or share/global stream or transcode concurrency limit reached |

Streams are throttled to the share's and the global bandwidth limit and end early when
a quota is used up mid-stream.

## Local proxy (receiving server)

### `GET|HEAD /P2PShare/Proxy/{connectionId}/{remoteItemId}?key=…[&transcode=true&maxBitrate=…&startTimeTicks=…]`

Target of `.strm` files. `key` is the connection's random proxy key; a wrong key returns
`404`. Forwards `Range` to the peer and passes status, `Content-Type`,
`Content-Length`, `Content-Range`, `Accept-Ranges`, `Last-Modified` and `ETag` back.
`502` when the peer cannot be reached; peer errors (e.g. `429`) are passed through.

## Admin API (administrators only)

Requires a Jellyfin administrator session (`RequiresElevation` policy). Used by the
dashboard page.

| Method & path | Body | Result |
|---|---|---|
| `GET /P2PShare/Admin/Settings` | — | `{ settings, serverName, suggestedPublicUrl, mirrorRoot }` |
| `PUT /P2PShare/Admin/Settings` | `GlobalSettings` | Saved settings, or `400 { error, errors[] }` |
| `GET /P2PShare/Admin/Libraries` | — | `[{ id, name, collectionType, isMirror }]` |
| `GET /P2PShare/Admin/Status` | — | `{ global: usage, activeStreams[], syncRunning }` |
| `GET /P2PShare/Admin/Shares` | — | Share views with `usage` and `activeStreams` |
| `POST /P2PShare/Admin/Shares` | `ShareEdit` | `201 { share, token }` — token shown once |
| `PUT /P2PShare/Admin/Shares/{id}` | `ShareEdit` | Share view |
| `POST /P2PShare/Admin/Shares/{id}/Regenerate` | — | `{ share, token }`; old token invalid |
| `DELETE /P2PShare/Admin/Shares/{id}` | — | `204` |
| `GET /P2PShare/Admin/Connections` | — | Connection views (token not included) |
| `POST /P2PShare/Admin/Connections/Preview` | `{ token }` | `{ serverUrl, info, libraries }` |
| `POST /P2PShare/Admin/Connections` | `{ token }` | `201` connection view; sync queued. `409` if already added |
| `PUT /P2PShare/Admin/Connections/{id}` | `{ enabled, preferTranscode, preferredMaxBitrateMbps, libraries: [{ remoteLibraryId, enabled, localName }] }` | Connection view; sync queued |
| `POST /P2PShare/Admin/Connections/{id}/Sync` | — | `202` |
| `DELETE /P2PShare/Admin/Connections/{id}` | — | `204`; libraries and mirror folders removed |

`ShareEdit`:

```json
{
  "name": "Bob",
  "libraryIds": ["f137a2dd-21bb-c1b9-9aa5-c0f6bf02a805"],
  "enabled": true,
  "expiresAt": "2027-01-01T00:00:00Z",
  "allowTranscoding": true,
  "maxStreamingBitrateMbps": 20,
  "maxBandwidthMbps": 50,
  "quotaGB": 200,
  "quotaInterval": "Monthly",
  "maxConcurrentStreams": 2,
  "allowedNetworks": ["203.0.113.0/24"],
  "notes": ""
}
```

## Invite token format

```
jfp2p1.<base64url(JSON)>.<first 8 chars of base64url(SHA-256(<base64url(JSON)>))>
JSON: { "v": 1, "u": "<server url>", "n": "<server name>", "sid": "<server id>", "i": "<share id N>", "s": "<secret>" }
```
