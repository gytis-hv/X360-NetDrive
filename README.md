# X360-NetDrive

X360-NetDrive uses a Raspberry Pi Zero 2 W to expose network-hosted Xbox 360
content as read-only USB mass storage over Wi-Fi:

- **WebDAV folders:** expose up to eight folders from a WebDAV server as USB drives.
- **Direct ISO:** a separate .NET server presents ISO contents as a virtual FAT32
  disk; the Pi streams that disk over HTTP as one USB drive.

It does not sign or license game content. Stock consoles remain subject to
normal content restrictions. Direct `default.xex` launch has been tested on
a modified console with XBDM; GOD/SVOD projection is experimental.

## Documentation

- [Build and test](BUILDING.md): build the server and Raspberry Pi image from source.
- [Prepare and use](SETUP.md): flash, configure, connect, operate, and troubleshoot.
- [GitHub releases](BUILDING.md#github-releases): tagged builds and downloadable artifacts.
- [Server third-party notices](server/THIRD_PARTY_NOTICES.md): dependency licenses
  included with server builds and published output.

## Requirements

- Raspberry Pi Zero 2 W, microSD card (8 GB or larger), and a data-capable
  micro-USB cable.
- A 2.4 GHz Wi-Fi network and a reachable PC, NAS, or other host for your content.
- A trusted network: the ISO server has no authentication and must not be exposed
  to the internet.

Use only content you are entitled to use.

## Repository

- [server/](server/) contains the PC-side ISO/WebDAV server and .NET tests.
- [rpi/](rpi/) contains the Pi image builder, bridge, configuration, and tests.

The server and Pi image are built separately. Updating source or a running
device does not update an existing image; rebuild and reflash to include changes.

The project itself does not yet have a declared license. Third-party components
retain their own licenses; the Pi image includes Raspberry Pi OS and other packages
whose obligations are separate from the server notices.
