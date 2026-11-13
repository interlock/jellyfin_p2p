# Jellyfin P2P Share

A Jellyfin plugin that lets server operators share libraries with each other.

The sharing operator creates an **invite token** per peer and decides, per token, which
libraries are shared, whether transcoding is allowed, the maximum bitrate, the maximum
bandwidth and the data volume allowed per hour/day/week/month. Global limits cap the
server as a whole. The receiving operator pastes the token and the shared libraries
appear as normal libraries on their server.

* [Design document](docs/design.md)
* [User guide](docs/user-guide.md)
* [Peer & admin API](docs/api.md)
* [Security model](docs/security.md)
* [Development & testing](docs/development.md)

## Requirements

* Jellyfin 12.0.x on both servers
* The sharing server must be reachable from the receiving server (HTTPS recommended)

## How it works (short)

* **Sharing server**: exposes a token-authenticated peer API (`/P2PShare/Remote/*`) that
  lists the shared libraries and streams items — directly with HTTP ranges or transcoded
  via Jellyfin's ffmpeg — through token-bucket bandwidth limits, data quotas and
  concurrency limits.
* **Receiving server**: a scheduled task mirrors each shared library as a folder of
  `.strm` + `.nfo` + artwork files and registers it as a normal Jellyfin library. The
  `.strm` URLs point at a local proxy endpoint, so the share secret never leaves the
  plugin configuration.

## Build

```bash
dotnet build -c Release
dotnet test
```

The plugin DLL is at `src/Jellyfin.Plugin.P2PShare/bin/Release/net10.0/Jellyfin.Plugin.P2PShare.dll`.
Copy it into `<jellyfin data>/plugins/P2PShare_0.1.0.0/` and restart Jellyfin.

## License

Not chosen yet. Jellyfin plugins link against GPL-licensed Jellyfin assemblies and are
usually released under GPL-3.0; add a `LICENSE` file before publishing.
