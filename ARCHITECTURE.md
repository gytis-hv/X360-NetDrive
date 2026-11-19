# X360-NetDrive architecture

This document describes the current repository implementation, including
uncommitted work. It is not a description of every published image. The
[functional requirements](FRD.md) describe the agreed X360 Net Server-only
direction. Build and operating
instructions are in [BUILDING.md](BUILDING.md) and
[SETUP.md](SETUP.md).

## System overview

```text
                         control plane
  Browser -- HTTP --> Pi Go controller --> systemd services
                           |                    |
                           |              source and USB control
                           v                    v
                  saved profiles           Pi bridge
                                                |
                         data plane             |
  X360 Net Server -- HTTP disk ranges -----------> NBD block device
                                                           |
                                                           v
  Xbox 360 <-------- USB mass-storage gadget <-------- /dev/nbd0
```

The server generates the FAT disk and serves it using the HTTP manifest
and range-read protocol. Generic WebDAV sources are not supported.

The Pi presents one read-only, removable USB logical unit, LUN0. Only one
source is active at a time; the tested Xbox did not enumerate additional
LUNs. The Xbox assigns the visible drive name and may change it on reconnect.
USB visibility does not prove that a launcher can run every title.

## Host server

The required .NET server, **X360 Net Server**, indexes one ISO or a library
of ISOs at startup. It builds an in-memory
virtual file tree backed by reads from the original ISOs, rather than making
a complete extracted copy. The default game layout and collision rules are
specified in [FRD.md](FRD.md#x360-net-server-iso-folder-layout). The server
reports excluded files and warnings and uses a library snapshot identifier.
Changes to the library currently require a server restart and Pi reconnect.

The server exposes:

| Endpoint | Current purpose |
| --- | --- |
| `/health` | Readiness and indexing counts. |
| `/` | JSON manifest with the snapshot disk path, games, exclusions, and warnings. |
| `/exports/<snapshot>/disk` | Read-only, range-addressable virtual MBR/FAT32 disk used by the current HTTP disk path. |

The disk is generated on demand from the same virtual tree: the server
constructs partition and FAT metadata and maps data extents to virtual files.
Its FAT volume ID is derived from the snapshot; this ID is not proven to be
the Aurora device ID. Experimental GOD/SVOD projection and its cache are
optional and do not establish launcher compatibility.

Implementation: [Program.cs](server/src/X360NetDrive.Server/Program.cs),
[IsoLibrary.cs](server/src/X360NetDrive.Server/Library/IsoLibrary.cs),
[DiskEndpoint.cs](server/src/X360NetDrive.Server/Http/DiskEndpoint.cs),
and [Fat32Disk.cs](server/src/X360NetDrive.Server/Fat/Fat32Disk.cs).

## Pi data path

### Source selection and discovery

The controller checks the selected server manifest for a valid same-origin
read-only snapshot disk URL. Card-only settings use the same discovery. An
old snapshot URL is resolved to the server root before discovery. WebDAV
profiles remain saved but cannot connect; select an X360 Net Server instead.

### X360 Net Server disk path

The controller discovers a snapshot URL from the X360 Net Server manifest.
`nbdkit`'s curl plugin reads that disk using HTTP byte ranges. The server,
rather than the Pi, constructs its FAT32 layout.

### Common NBD and USB stages

For the server disk, the bridge first starts a raw NBD export. The
`x360-nbd-size.py` helper negotiates with that export and rejects an empty,
non-512-byte-aligned, or oversized source. A second `nbdkit` instance uses
the NBD plugin and truncate filter to present exactly
2,199,023,255,040 bytes (2 TiB minus one 512-byte sector). Reads beyond the
source return zeroes; the original partition and filesystem are not expanded.
The source is not truncated to fit. This fixed-capacity design remains under
investigation.

`nbd-client` connects the padded export to `/dev/nbd0`. The bridge loads
Linux `g_mass_storage` with that block device as a single removable,
read-only LUN. Its USB serial comes from the Pi's device-tree serial; vendor,
product, and manufacturer fields are currently fixed in the script. The USB
descriptor serial, FAT volume ID, Xbox drive name, and Aurora device ID are
distinct concepts and should not be assumed equivalent.

Implementation: [x360-netdrive.sh](rpi/scripts/x360-netdrive.sh),
[x360-nbd-size.py](rpi/scripts/x360-nbd-size.py).

## Control plane and lifecycle

The lightweight Go controller serves HTML forms and CSS without a frontend
runtime. Users add and remove profiles, select or unmount a source, reconnect
it, and temporarily attach or detach USB. The page shows desired selection,
systemd state, and recent logs; it cannot establish that the Xbox has
successfully mounted the disk.

Persistent profiles and the selected server are stored in
`/boot/firmware/x360-netdrive/servers.json`. The version-1 format retains eight
legacy assignment fields, but only one assignment is accepted for the fixed
drive. A sole older assignment maps to LUN0; multiple assignments are reported
as an error. When the file is absent, card settings select the server.
The runtime `source.conf`, NBD socket, and device list are kept under
`/run/x360-netdrive`; they are regenerated rather than being the persistent
source of truth.

On startup, systemd brings up Wi-Fi and the source service. The source
service runs the controller's `--prepare-sources` mode to resolve the saved
selection, then starts the bridge. The USB service requires the source service
and attaches the gadget only when a device is recorded. No selected managed
source means no USB media. Wi-Fi setup has its own retry policy; source
discovery and startup failures are surfaced, and automatic recovery from every
network or server failure is not guaranteed.

For a source switch, the controller preflights the prospective selection,
stops USB and the source, persists the new selection, and requests startup if
a source remains selected. This disconnects the Xbox drive. A failure after
the stop can leave USB detached or a saved selection not yet active; service
status and logs must be checked. Temporary USB detach leaves the selection and
source in place, while unmounting clears the saved selection.

Implementation: [main.go](rpi/control/main.go),
[servers.go](rpi/control/servers.go), and the
[systemd units](rpi/systemd/).

## Security and persistence boundaries

The control service reads its per-device admin token from bootfs and refuses
to start if the token is missing or invalid. Browser sign-in uses server-side
sessions, HttpOnly/SameSite cookies, and CSRF/origin checks; Basic
authentication remains available to command-line clients. The control service
runs with systemd filesystem and privilege restrictions but can invoke the
relevant systemd actions.

Plain HTTP does not protect the token or session on the network. The current
X360 Net Server has no authentication. Use a trusted network or the
documented HTTPS proxy arrangement, and do not expose these services to the
internet. The boot configuration contains Wi-Fi credentials and must be kept
private.

Public image builds use a read-only root overlay and do not enable interactive
login or SSH; a private debug image provides separate access. Bootfs holds
persistent configuration outside the root overlay. The overlay reduces
write exposure but does not guarantee protection from corruption.

Implementation: [auth.go](rpi/control/auth.go),
[build_image.sh](rpi/build_image.sh), and
[prepare-public-image.sh](rpi/scripts/prepare-public-image.sh).

## Target design and open questions

The server-generated FAT disk uses the existing HTTP manifest and range-read
protocol. Existing WebDAV settings need replacement with a server base URL;
they are not converted or deleted automatically.

In one hardware trial with 157 games, the Pi-generated WebDAV disk reached
FAT preparation and USB attachment after about seven and a half minutes.
Available memory fell below 46 MiB during startup and the Pi became slow to
respond; the trial was stopped and the HTTP source restored. The trial did not
verify that the full WebDAV-backed library could be read by the Xbox.

Further design and validation are needed for:

- Stable Aurora device identity per saved server profile across reboots and
  source switches. Fixed capacity removes one observed variable, but identity
  inputs and a reliable mechanism are not yet established.
- The fixed-capacity and padding approach, FAT32 geometry, large libraries,
  resource usage, and behavior near capacity limits.
- Server authentication and transport protection; requirements and mechanisms
  have not yet been decided.
- Control-page SSH enablement, key upload or generation, HTTPS provisioning
  for private-key downloads, key recovery, and login log retention.
- Dynamic library updates, including consistency and safety while the Xbox
  is reading.

The public image and server artifacts must be rebuilt and released to include
repository changes; a deployed Pi or local working tree is not a published
release.
