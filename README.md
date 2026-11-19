# X360-NetDrive

X360-NetDrive uses a Raspberry Pi Zero 2 W to expose network-hosted Xbox 360
content as read-only USB mass storage over Wi-Fi. **X360 Net Server** builds a
virtual FAT32 disk from an ISO library and streams disk blocks to the Pi using
the project's HTTP-based protocol; it does not create a full disk image.

The current repository supports X360 Net Server only, not generic WebDAV sources.
Older published artifacts may still contain the WebDAV path.

The Pi exposes one fixed USB drive. Save multiple servers in the control page
and switch which one backs that drive; simultaneous USB LUNs are not supported.
The advertised disk capacity is fixed just below 2 TiB across sources; this
does not expand the source filesystem.

It does not sign or license game content. Stock consoles remain subject to
normal content restrictions. Direct `default.xex` launch has been tested on
a modified console with XBDM; GOD/SVOD projection is experimental.

## Documentation

- [Functional requirements](FRD.md): draft release targets and open decisions.
- [Architecture](ARCHITECTURE.md): current data paths, lifecycle, and planned changes.
- [Build and test](BUILDING.md): build the server and Raspberry Pi image from source.
- [Prepare and use](SETUP.md): flash, configure, connect, operate, and troubleshoot.
- [Pi web control](SETUP.md#enable-web-control): manage network sources and USB attachment.
- [GitHub releases](BUILDING.md#github-releases): tagged builds and downloadable artifacts.
- [Server third-party notices](server/THIRD_PARTY_NOTICES.md): dependency licenses
  included with server builds and published output.

## Requirements

- Raspberry Pi Zero 2 W, microSD card (8 GB or larger), and a data-capable
  micro-USB cable.
- A 2.4 GHz Wi-Fi network and a reachable host running X360 Net Server.
- A trusted network: the current server has no authentication and must not be exposed
  to the internet.

Use only content you are entitled to use.

## Repository

- [server/](server/) contains X360 Net Server and its .NET tests.
- [rpi/](rpi/) contains the Pi image builder, bridge, configuration, and tests.

The server and Pi image are built separately. Updating source or a running
device does not update an existing image; rebuild and reflash to include changes.

The project itself does not yet have a declared license. Third-party components
retain their own licenses; the Pi image includes Raspberry Pi OS and other packages
whose obligations are separate from the server notices.
