# Project: X360-NetDrive
**Wi-Fi WebDAV-to-USB Mass Storage Bridge for Xbox 360 (Raspberry Pi Zero 2 W)**

---

## 1. Overview & Architecture
**X360-NetDrive** turns a Raspberry Pi Zero 2 W into a wireless, plug-and-play USB Mass Storage dongle for the Xbox 360. The Pi connects to a local Wi-Fi network, mounts up to 8 remote WebDAV directories (`Xbox_LUN1` through `Xbox_LUN8`), synthesizes virtual FAT32 partitions on the fly in user space, and exposes them over USB OTG (`g_mass_storage`) as standard local USB drives (`Usb0:`–`Usb7:`).

```text
[Windows/NAS WebDAV Server]
   ├── /Xbox_LUN1 (Extracted XEX / GOD folders, <4GB per file)
   ├── /Xbox_LUN2
   └── ... (up to /Xbox_LUN8)
          │
          ▼  (Wi-Fi 802.11n / HTTP Byte-Range Requests)
[Raspberry Pi Zero 2 W - 512MB RAM]
   ├── rclone mount (GOGC=25, --vfs-cache-mode off) -> /mnt/lun1..8
   ├── nbdkit floppy + cow filter (FAT32) -> /tmp/nbd1..8.sock
   ├── nbd-client -> /dev/nbd0..7; x360-fat32.py validates/corrects boot headers
   └── modprobe g_mass_storage file=/dev/nbd0,/dev/nbd1,...
          │
          ▼  (Micro-USB OTG Data Port -> Xbox 360 USB Port)
[Xbox 360 Console]
   └── Sees Usb0:, Usb1:, ... as native FAT32 external storage
```

### Alternative: PC-Generated Virtual Disk

`src/X360NetDrive.Server` is a .NET 10 read-only ISO/WebDAV server. `--iso-root <directory>` recursively
exposes a library, preserving relative ISO paths as directories under `Games/`; `--iso <file>` retains
single-game mode. Library mode logs and reports unsupported ISOs in the manifest's `excluded` array,
serves the supported entries, and fails startup if none remain. Its immutable virtual-file
tree backs both `/webdav/` and an HTTP-range MBR/FAT32 export. ISO file extents are read
positionally from a persistent source handle, rather than extracted or copied into a disk image.
GOD/SVOD generation remains available but is opt-in with `--god true`; the default skips its hash scan and cache.
The manifest at `/` advertises the snapshot-specific export URL and a per-disc `games` list with launch paths.

Setting `HTTP_DISK_URL` selects one direct HTTP-backed LUN:
`nbdkit curl -> nbd-client -> g_mass_storage`. This bypasses the Pi's rclone/FUSE/floppy/COW
pipeline and ignores `LUN_COUNT`; leaving the option empty preserves the original architecture.
The PC-generated FAT32 volume enforces minimum cluster sizing without an on-disk padding file.
Source files and layout must remain unchanged while mounted. Restart/reconnect explicitly to
select a different snapshot. The HTTP server is unauthenticated and must stay on a trusted LAN.

### Key Design Goals & Hardware Constraints
- **Zero-Mod Console Compatibility:** Works on both stock and RGH/JTAG Xbox 360 consoles without custom DashLaunch plugins.
- **Turnkey Distributable Image:** End users flash the `.img` file, edit a single plain-text file (`x360-netdrive.txt`) on the FAT32 `bootfs` partition via Notepad/TextEdit, and plug the Pi into the console.
- **Power-Loss Safe:** Uses `overlayfs` (read-only root filesystem) and compressed `lz4` ZRAM swap (`256 MB`) so abruptly cutting power when turning off the Xbox 360 never corrupts the SD card.
- **512 MB RAM Optimized:** Streams HTTP byte-range requests directly (`--vfs-cache-mode off`, `GOGC=25`, `--buffer-size 4M`) without disk-backed caching in `/tmp`.
- **Storage Limits:**
  - Xbox 360 USB storage requires MBR + FAT32 (**4 GB max single file size**; **2 TB max per LUN**).
  - Linux `g_mass_storage` hard maximum (`FSG_MAX_LUNS`) is **8 LUNs** (`nbd0`–`nbd7`).
  - Because `nbdkit floppy` constructs the virtual FAT32 table in RAM, keep each WebDAV folder around **250–400 GB** (split across up to 8 LUNs) on a 512 MB Pi Zero 2 W.
  - FAT32 needs at least **65,525 data clusters**. Small test shares need a **1 GiB sparse padding file** per LUN.
  - The floppy plugin's zero boot jump is incompatible with Xbox. A small COW overlay corrects the primary and backup boot jumps; game reads are not copied into the overlay.
- **Wiring Precaution:** Connect only the inner **USB** data port on the Pi Zero 2 W to the Xbox 360. Do NOT connect an external wall charger to `PWR IN` simultaneously to avoid 5V power backfeed.

---

## 2. Included Files in This Bundle
- `PROJECT_SPEC.md` — Complete technical blueprint and context for the build/coding agent.
- `README.md` — End-user setup guide (flashing, configuring `x360-netdrive.txt`, hosting WebDAV on Windows/Linux).
- `build_image.sh` — Host build script (Ubuntu/Debian/WSL2) that downloads Raspberry Pi OS Lite, injects packages and scripts via `systemd-nspawn`, enables `overlayfs`, and outputs a ready-to-flash `.img`.
  Requires a private `userconf.txt` path (or `config/userconf.txt`) containing a username and SHA-512 crypt password hash. The account is provisioned before enabling the read-only overlay; SSH is not enabled by default.
- `config/x360-netdrive.txt` — Template configuration file placed on `/boot/firmware/x360-netdrive.txt`.
- `scripts/x360-netdrive.sh` — Runtime service script executed on boot (handles CRLF stripping, independent NetworkManager Wi-Fi setup via `--wifi-only`, `LUN_COUNT=auto` discovery, `rclone` WebDAV mounts, `nbdkit` FAT32 synthesis, and `g_mass_storage` attachment).
- `scripts/x360-fat32.py` — Validates FAT32 cluster count and repairs zero boot jumps on the writable COW-backed NBD export before read-only USB exposure.
- `src/X360NetDrive.Server` — .NET 10 XDVDFS parser, read-only WebDAV, virtual unsigned GOD/SVOD files, and generated HTTP-range FAT32 disk; original ISO payload is not copied. GOD dashboard launch remains experimental; direct XEX launch through an existing XBDM plugin has been verified.
- `systemd/x360-netdrive.service` — Systemd unit file starting the bridge after `network-online.target`.
- `systemd/x360-netdrive-wifi.service` — Configures Wi-Fi before `network-online.target` independently of bridge success, and retries connection failures.
- `scripts/x360-netdrive-diagnostics.sh` and `systemd/x360-netdrive-diagnostics.*` — Optional `HEADLESS_DEBUG=1` snapshots to `bootfs/x360-debug.log`; debug builds also enable SSH using the configured account. Periodic boot-partition writes weaken power-loss protection.

---

## 3. Agent Implementation Checklist
If extending or refining this repository with an external coding agent:
1. **Verify `build_image.sh` Execution:** Test on an Ubuntu 22.04/24.04 or WSL2 host with `qemu-user-static`, `systemd-container`, `parted`, and `wget` installed.
2. **Optional PiShrink Integration:** Append `PiShrink` (`pishrink.sh -z`) at the end of `build_image.sh` to compress the final `.img` below ~1.5 GB for distribution.
3. **Optional Captive Portal Fallback:** Add `comitup` or a lightweight Python HTTP fallback if Wi-Fi association fails after 20 seconds.
4. **Optional Web Reload Endpoint:** Expose a minimal HTTP endpoint on port `8088` (`http://x360-netdrive.local:8088/reload`) to unload `g_mass_storage`, restart `nbdkit` + `rclone`, and re-attach USB drives when new games are added on the host PC.
