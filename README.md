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

## Build

```bash
dotnet build -c Release
dotnet test
```

The plugin DLL is at `src/Jellyfin.Plugin.P2PShare/bin/Release/net10.0/Jellyfin.Plugin.P2PShare.dll`.
Copy it into `<jellyfin data>/plugins/P2PShare_0.1.0.0/` and restart Jellyfin.

## License

GPL-3.0 (to match Jellyfin plugin conventions).
