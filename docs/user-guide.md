# P2P Share — User Guide

This guide is for Jellyfin server administrators. Both servers need Jellyfin 12.0 or
newer with the P2P Share plugin installed.

In this guide **Alice** shares her libraries and **Bob** receives them.

## 1. Install the plugin

On **both** servers:

1. **Dashboard → Plugins → Manage Repositories** → add
   `https://raw.githubusercontent.com/interlock/jellyfin_p2p/main/manifest.json`.
2. **Dashboard → Plugins → Catalog** → **P2P Share** → **Install**, then restart Jellyfin.

**P2P Share** then appears in the dashboard menu. For manual installation see the
[README](../README.md#manual-installation).

## 2. Make the sharing server reachable (Alice)

Bob's server must be able to open HTTP(S) connections to Alice's server. Typical setups:

* A reverse proxy with a domain and TLS certificate (recommended), e.g.
  `https://media.alice.example`.
* A VPN or overlay network (WireGuard, Tailscale, ZeroTier) — Bob uses Alice's VPN address.
* A port forward to Jellyfin's HTTP port (only with HTTPS; tokens are credentials).

Only the `/P2PShare/Remote/*` endpoints are used by Bob's server.

## 3. Configure the sharing side (Alice)

Open **Dashboard → P2P Share → Settings**:

| Setting | What to put |
|---|---|
| Public server URL | The address from step 2, e.g. `https://media.alice.example`. Embedded in every token. Use **"Use this page's address"** if you are browsing through that same URL. |
| Name shown to peers | Optional friendly name. |
| Require HTTPS | Turn on if your public URL is HTTPS (recommended). |
| Total bandwidth to peers | Cap on outbound throughput for all shares together (Mbps). |
| Total data to peers + period | Cap on data volume for all shares together (GB per hour/day/week/month). |
| Max concurrent peer streams / transcodes | Protect your CPU and uplink. |
| Allow transcoding for peers | Master switch. When off, no share can transcode. |
| Default share lifetime | New shares expire after this many days unless you set a date. |

Save.

## 4. Create a share (Alice)

**Outgoing shares → New share**:

* **Name** — who it is for, e.g. "Bob".
* **Libraries** — tick the libraries to share. Libraries that you yourself receive from
  other servers cannot be re-shared and are not listed.
* **Allow transcoding** — lets Bob ask your server to transcode (uses your CPU/GPU).
* **Max streaming bitrate** — items with a higher bitrate are transcoded down to this
  value (if transcoding is allowed) or refused.
* **Max bandwidth** — throughput cap for all of Bob's streams together.
* **Data quota + period** — e.g. 200 GB monthly. Streams are refused (and running streams
  stopped) when it is used up; it resets at the start of the next period (UTC).
* **Max concurrent streams**, **Expires**, **Allowed IPs / CIDR ranges** (e.g. Bob's
  static IP or VPN subnet), **Notes**.

Click **Save**. The **invite token** is shown once. Copy it and send it to Bob over a
private channel. If it is lost or leaked, click **New token** on the share: the old token
stops working immediately, and Bob must re-add the share with the new one.

Each share card shows its status, usage this period, lifetime usage, last activity and
how many streams are running. **Disable** pauses a share; **Revoke** deletes it.

## 5. Add the share (Bob)

On Bob's server, **Dashboard → P2P Share → Incoming connections**:

1. Paste the token and click **Check token** to see Alice's server name, libraries and
   the limits Alice set.
2. Click **Add**. A sync starts in the background.

After the sync, each of Alice's libraries appears as a normal library on Bob's server,
named with the template from Settings (default `{library} ({server})`, e.g.
"Movies (Alice's Jellyfin)"). Jellyfin then scans it and fetches metadata as usual.

Per connection Bob can:

* untick libraries he doesn't want (their local library is removed),
* rename a library (Local name),
* ask Alice's server to transcode, optionally at a chosen bitrate,
* **Sync now**, disable, or **Remove** the connection (removes the libraries).

Bob controls which of his users see the new libraries through normal Jellyfin user
library access settings.

### Receiving-side settings (Bob)

| Setting | Notes |
|---|---|
| Local proxy base URL | Written into the `.strm` files. Default `http://127.0.0.1:8096`. Change the port if Jellyfin uses another one, or use the LAN/public address if some clients try to direct play the `.strm` URL themselves (see Troubleshooting). Changing it rewrites all `.strm` files on the next sync. |
| Sync interval | How often the scheduled task **Sync shared libraries from peers** runs (Dashboard → Scheduled Tasks). |
| Download artwork | Copies posters/backdrops from Alice. Off = Jellyfin fetches artwork from internet providers. |
| Library name template | `{library}` and `{server}` placeholders. |
| Mirror folder | Where the `.strm` trees are written. Default is inside the plugin data folder. |

## 6. Monitoring (Alice)

**Activity** shows total usage this period and every running peer stream (share, item,
direct/transcode, peer address, bytes sent).

## 7. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| "Could not reach …" when adding a token | Alice's public URL is not reachable from Bob's server. Test with `curl https://media.alice.example/P2PShare/Remote/Info` from Bob's machine — a 401 JSON response means it is reachable. |
| "The peer does not expose the P2P Share API" | Plugin not installed/updated on Alice's server, or a reverse proxy rule blocks `/P2PShare/`. |
| "Invalid share credentials" | Token was regenerated or the share revoked. Ask for a new token. |
| "This share has expired" / "disabled" | Alice must extend or enable the share. |
| "Requests from this address are not allowed" | Alice's IP allowlist doesn't include Bob's server's public address. |
| Playback fails with quota / too many streams | Limits reached; see the share's usage on Alice's side. |
| Libraries appear but nothing plays on some clients | The client tried to open the `.strm` URL directly. Set **Local proxy base URL** to an address clients can reach (e.g. `http://192.168.1.10:8096` or Bob's public URL), save and **Sync now**. |
| Items have no metadata | Alice's items lack provider IDs; enable internet metadata providers on Bob's mirror library, or turn on Download artwork. |
| Music plays but shows little metadata | Audio `.strm` items carry only names and track numbers. See limitations below. |

Logs: both plugins log under the `Jellyfin.Plugin.P2PShare` category.

## 8. Limitations (v0.1)

* The sharing server must be directly reachable (no relay/NAT traversal).
* Watch state is not synchronized between servers.
* External subtitle files are not transferred (embedded subtitles work in direct play).
* Transcoded peer streams (H.264/AAC MPEG-TS) are not byte-seekable; Jellyfin seeks by
  restarting the stream at a time offset.
* Music libraries are mirrored with limited metadata.
* Data quotas count bytes as they are sent, so a stream can overshoot by one chunk
  (≤ 64 KB). Bob's Jellyfin probing a file during a scan also counts.
