# X360-NetDrive functional requirements

Status: draft for review. These requirements describe the intended product;
they are not a claim that every feature has been implemented or verified.

## 1. Purpose and scope

X360-NetDrive lets an Xbox 360 access network-hosted content through a
Raspberry Pi presenting a USB storage device. The product includes a Pi image,
a browser-based control page, and a required **X360 Net Server**.
Source code and built releases are to be available on GitHub.

## 2. Supported environment

- **FR-01:** The bridge targets a Raspberry Pi Zero 2 W connected to an
  Xbox 360 by USB and to the network by Wi-Fi.
- **FR-02:** The bridge presents one read-only USB drive. Only one saved source
  is mounted at a time.
- **FR-03:** The Xbox chooses the drive name, such as `USB0:`. X360-NetDrive
  does not guarantee that name.
- **FR-04:** The bridge does not bypass Xbox content restrictions or sign or
  license game content.

## 3. Content sources

- **FR-05:** The Pi connects only to X360 Net Server using the project's
  server-to-Pi protocol. Generic WebDAV sources are out of scope.
- **FR-06:** The user provides an X360 Net Server base URL; the Pi discovers
  the server's current disk snapshot without requiring a snapshot URL.
- **FR-07:** A saved server profile identifies an X360 Net Server source.
  The selected server's virtual disk is presented as the USB drive.
- **FR-08:** X360 Net Server scans a directory of ISO files, constructs the
  virtual FAT disk layout, and serves disk data on demand without creating
  a full extracted copy or disk image.
- **FR-09:** X360 Net Server reports excluded ISOs and warnings. For now, changes to
  the ISO library require restarting the server and reconnecting the Pi.
  Dynamic updates are a future requirement; their behavior and safety while
  the drive is in use remain to be defined.
- **FR-10:** ISO contents are presented as files, not as an emulated physical
  disc. GOD/SVOD projection is experimental.

The existing HTTP manifest and range-addressed disk endpoint provide the
current implementation of the server-to-Pi protocol. Generic WebDAV input
is no longer supported by the current repository code.

### X360 Net Server ISO folder layout

These are the current server's layout rules, included for review as product
requirements.

- **FR-31:** Scan the configured ISO library recursively, without following
  symbolic links or junctions. Expose each supported ISO's contents in its own
  folder under `Games/ISOs/`.
- **FR-32:** By default, name the folder using the embedded display title and
  available metadata, in this order:
  `Title [TitleID][Disc N of M][MediaID]`. IDs use eight uppercase hexadecimal
  digits. Omit unavailable IDs; omit the disc group when its number or count is
  unavailable or both are 1. Do not emit empty brackets.
- **FR-33:** If the display title cannot be read, use the ISO filename without
  its extension and report a warning. A user-specified title can override the
  display title in single-ISO mode.
- **FR-34:** Normalize folder titles for FAT compatibility: replace invalid
  characters with underscores, limit the title component to 160 UTF-16 code
  units without splitting a character, trim leading and trailing whitespace
  and trailing periods, and use `Untitled` if nothing remains. Report a warning
  when the title changes; do not modify the source ISO.
- **FR-35:** Resolve folder-name collisions case-insensitively without merging
  games. When a collision is found, number the first folder `(1)` and subsequent
  folders `(2)`, `(3)`, and so on, and report a warning. Rebuilding an unchanged
  library should produce the same paths; adding or removing a collision may
  change existing paths.
- **FR-36:** Offer the existing optional source-relative layout under `Games/`
  instead of the default metadata-based layout.

For example, `Games/ISOs/Example [12345678][Disc 1 of 2][ABCDEF01]/`
contains that ISO's projected files, including its `default.xex` when present.
The server presents the tree through the virtual FAT disk.

## 4. Server profiles and USB control

- **FR-11:** Users can save multiple named server profiles, including servers
  that are currently offline.
- **FR-12:** Users can mount one saved profile, replacing the currently mounted
  source.
- **FR-13:** Users can unmount a source without deleting its profile and can
  delete an unmounted profile.
- **FR-14:** Users can temporarily detach and reattach USB without deleting
  the selected profile.
- **FR-15:** Before a switch or detach, the page requires confirmation that
  the Xbox has stopped using the drive.
- **FR-16:** Profiles and the selected source persist across Pi reboots. The
  selected source is configured to reconnect at startup.
- **FR-17:** The page displays the selected profile, source and USB service
  status, errors, and recent bridge logs. A selected profile alone is not
  proof that the Xbox sees the drive.

## 5. Disk capacity and identity

- **FR-18:** The Pi advertises a fixed USB disk capacity of 2 TiB minus one
  512-byte sector, regardless of the selected source.
- **FR-19:** Smaller source disks are padded with zeroes. This does not enlarge
  their filesystem or usable free space. Oversized or non-sector-aligned
  sources are rejected rather than truncated. This requirement remains
  provisional pending further investigation.
- **FR-20:** Each saved server profile should have a stable device identity,
  so Aurora can retain separate scan paths for different servers.
- **FR-21:** A profile's Aurora device ID should remain stable across Pi
  reboots and reconnections.
- **FR-22:** Switching away from a server and back should restore that
  server's previous Aurora device ID.

FR-20 through FR-22 are agreed targets, not verified behavior. Fixed capacity
removes one observed cause of ID changes, but the other factors and the
mechanism for controlling Aurora's identity remain under investigation.
Existing scan paths may need updating when the advertised capacity changes.

## 6. Setup and security

- **FR-23:** Users configure Wi-Fi and a unique control-page token on the SD
  card before use.
- **FR-24:** The control page requires sign-in. The public image does not
  include a shared default token.
- **FR-25:** The current design is for a trusted network. Plain HTTP does not
  encrypt control-page credentials or traffic.
- **FR-26:** The previous requirement for authenticated WebDAV sources is
  superseded. Authentication and transport protection for X360 Net Server
  remain to be decided.
- **FR-27:** Users can enable and manage SSH through the control page without
  requiring existing SSH access:
  - The Pi generates a unique SSH host key on first boot and retains it across
    reboots.
  - Users can upload a user-generated public login key, or generate a login key
    through the page and download its private key.
  - Downloading a generated private key requires HTTPS.
  - Users can replace login keys and view successful and failed SSH login
    attempts.
  - Host-key regeneration is a separate action and warns that SSH clients will
    see a changed host identity.

SSH management is a release target, not a current public-image feature.
Server authentication, HTTPS provisioning, key-recovery behavior, and SSH
log retention remain to be specified.

## 7. Release and documentation

- **FR-28:** Publish source code on GitHub and provide downloadable Pi images
  and PC server builds.
- **FR-29:** Provide setup, operation, troubleshooting, and build instructions,
  checksums, and applicable third-party notices.
- **FR-30:** Validate releases with automated tests and hardware checks. A
  successful build alone does not establish hardware compatibility.

## 8. Constraints and open decisions

- FAT32 limits individual files to less than 4 GiB. Virtual disk layout and
  resource limits need validation with the intended libraries.
- Source content should not be changed while the Xbox is using it until safe
  dynamic-update behavior is defined.
- Device identity and capacity behavior need further investigation and
  hardware testing.
- The project license is undecided.
- The existing released image does not yet include all current uncommitted
  control-page and fixed-capacity work.

Current build and setup instructions are in [BUILDING.md](BUILDING.md) and
[SETUP.md](SETUP.md); they describe the implementation as it exists, not
necessarily all targets in this document.
