# P2P Share — Security Model

## Assets

* **Media** on the sharing server — only the libraries in a share may be read.
* **Share secrets** — bearer credentials granting access to a share.
* **Bandwidth, CPU and data allowance** of the sharing server.
* **Proxy keys** on the receiving server.

## Trust boundaries

| Party | Trusted for |
|---|---|
| Sharing admin | Everything on the sharing server |
| Receiving admin (token holder) | Reading the shared libraries within the share's limits |
| Receiving server's Jellyfin users | Whatever the receiving admin grants them via normal library access |
| Network | Nothing — use HTTPS |

## Controls

### Authentication

* 256-bit random secrets from `RandomNumberGenerator`.
* Only `SHA-256(secret)` is stored on the sharing server; the token is shown once.
* Constant-time comparison; unknown share ids still perform a hash comparison and
  return the same 401 as a wrong secret, so share ids cannot be enumerated.
* Secrets travel in a custom header (`X-P2PShare-Key`), never in URLs, so they do not
  end up in access logs or `.strm` files.
* Rotation (**New token**) invalidates the previous secret immediately; **Revoke**
  deletes the share; **Disable** and **Expiry** block access without deleting.
* Optional per-share IP/CIDR allowlist. IPv4-mapped IPv6 addresses are normalized. If
  Jellyfin is behind a reverse proxy, configure Jellyfin's *Known proxies* so the client
  address is correct.

### Authorization

* Every catalog, image and stream request verifies that the item belongs to one of the
  share's libraries (via Jellyfin's collection folders), not just that the id exists.
* Items backed by `.strm` files, shortcuts or remote URLs are never served, preventing
  re-sharing chains and SSRF through crafted remote paths.
* Libraries received from peers cannot be added to a share.
* Admin endpoints require Jellyfin's `RequiresElevation` (administrator) policy.

### Resource protection

* Per-share and global bandwidth caps (token buckets), data quotas per interval,
  concurrent stream limits, and a global concurrent transcode limit.
* Quotas are checked before a stream starts and enforced per chunk while it runs.
* Page sizes are clamped (≤ 1000 items).

### Receiving side

* `.strm` files contain only a local proxy URL with a per-connection random key, never
  the share secret.
* The proxy endpoint returns `404` for a wrong key (no oracle) and compares keys in
  constant time.
* The token (including the secret) is stored in the plugin's XML configuration, readable
  only by Jellyfin administrators and the OS account running Jellyfin. The admin API
  never returns stored tokens.
* Mirror writes are confined to the mirror folder: every path is sanitized and
  resolved against the root, and anything that escapes it is rejected. Only files listed
  in the folder's manifest are ever deleted.
* Peer responses are size-bounded by paging and parsed as typed JSON.

## Known risks and recommendations

| Risk | Mitigation |
|---|---|
| Token sent over plain HTTP can be sniffed | Use HTTPS (reverse proxy) and turn on **Require HTTPS**. |
| Token leaked by the receiving admin | Rotate or revoke the share; use an IP allowlist; set an expiry and a quota. |
| Proxy key visible to clients that direct-play `.strm` URLs | The key only grants what the receiving server already exposes for that connection. Prefer the default loopback proxy URL where clients allow it. |
| Sharing server exposed to the internet | Only `/P2PShare/Remote/*` needs to be reachable by peers; restrict other paths at the reverse proxy if desired. |
| CPU exhaustion via transcoding | Keep transcoding off by default, cap concurrent transcodes, set bitrate caps. |
| Stale access after a falling-out | Revoke the share; the receiving server's next sync marks the connection as access denied and stops playback. Mirror files remain until the receiving admin removes the connection. |

## Reporting

Please report vulnerabilities privately via GitHub security advisories on the repository.
