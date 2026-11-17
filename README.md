# X360-NetDrive

Stream your Xbox 360 game library over Wi-Fi using a **Raspberry Pi Zero 2 W** acting as a multi-drive USB Mass Storage dongle. Works on both stock and RGH/JTAG consoles without any console-side plugins.

---

## What You Need
- **Raspberry Pi Zero 2 W** + microSD card (8 GB or larger)
- **Micro-USB data cable** (must support data, not charge-only)
- **Windows PC, Mac, or NAS** hosting your game folders over WebDAV

---

## Quick Start Guide

### Step 1: Prepare Your Game Folders on PC
1. Create a folder on your PC (e.g., `D:\Xbox360`).
2. Inside it, create subfolders named `Xbox_LUN1`, `Xbox_LUN2`, etc. (up to `Xbox_LUN8`).
3. Place your extracted Xbox 360 games (`default.xex`) or GOD (Games on Demand) containers inside those folders.
   - *Note:* Because the Xbox 360 uses FAT32 over USB, no single file inside a game folder can exceed **4 GB** (extracted XEX and GOD formats naturally stay well below 4 GB).
   - Keep each `Xbox_LUN` folder under **~350 GB** so the Pi's 512 MB RAM easily holds the virtual file table.
   - Very small test folders need a **1 GiB sparse padding file** (for example `X360PAD.BIN`) inside each LUN. The virtual FAT32 filesystem requires at least 65,525 data clusters; the bridge rejects smaller layouts rather than exposing a disk Xbox cannot mount.
4. Start a read-only WebDAV server using [Rclone](https://rclone.org/):
   ```powershell
   rclone serve webdav D:\Xbox360 --addr 0.0.0.0:8080 --read-only
   ```

### Step 2: Flash & Configure the SD Card
1. Flash `x360-netdrive.img.xz` to your microSD card using **Raspberry Pi Imager** or **balenaEtcher**.
   - *Important:* Leave Raspberry Pi Imager's "OS Customisation" disabled.
   - The image builder preconfigures a local account, so no first-boot username dialog is required. Use the credentials chosen when building the image. SSH is not enabled by default.
2. Re-insert the SD card into your computer and open the **`bootfs`** drive.
3. Open **`x360-netdrive.txt`** in Notepad/TextEdit and fill in your Wi-Fi and PC IP details:
   ```ini
   WIFI_SSID="Your_WiFi_Name"
   WIFI_PASSWORD="Your_WiFi_Password"
   WIFI_COUNTRY="US"

   SERVER_URL="http://192.168.1.50:8080"
   LUN_COUNT=auto
   ```
4. Save the file and safely eject the SD card.

### Step 3: Connect to the Xbox 360
1. Insert the microSD card into the Raspberry Pi Zero 2 W.
2. Plug the micro-USB cable into the Pi's **inner `USB` port** (NOT `PWR IN`) and connect the other end to any USB port on your Xbox 360.
   - **Warning:** Do not connect a separate power adapter to `PWR IN` while plugged into the Xbox 360.
3. Turn on the Xbox 360. Within ~25 seconds, the Pi will connect to Wi-Fi and mount `XBOX_USB1`, `XBOX_USB2`, etc. as local USB storage drives.
4. When finished playing, simply turn off the Xbox 360—the Pi's filesystem is locked in read-only mode, so sudden power loss will never corrupt your SD card.

The bridge corrects the synthetic FAT32 boot headers through a small local copy-on-write overlay before connecting USB.
Game reads still stream from WebDAV; the overlay does not cache game data, and Xbox access stays read-only.
The Pi's hardware serial number provides a stable USB identity. Restart the Pi after changing the share's directory contents.

## C# ISO Server (Direct HTTP Disk Mode)

The .NET 10 server presents Xbox 360 XDVDFS ISOs as a read-only virtual library.
It exposes files through WebDAV and generates an MBR/FAT32 disk as an HTTP byte-range resource.
There is no full extracted game or disk-image copy. Source ISOs stay open read-only;
keep them unchanged while the Pi is connected.

GOD/SVOD generation is disabled by default; the normal library exposes only the virtual extracted-file view.
Use `--god true` to additionally expose an experimental GOD/SVOD package under `Content/`.
When enabled, each startup scans the game partition and writes hash-block metadata to
`%LOCALAPPDATA%\X360-NetDrive\server-cache` (about 36 MiB for a 7.5 GB partition),
not a converted copy of the game. Use `--cache <directory>` to relocate it.
Keep the metadata available while serving.
Graceful shutdown deletes that run's metadata file; forced termination can leave it behind.
These packages are unsigned and unlicensed: dashboard/XAM launch compatibility is experimental,
and must not be inferred from successful direct `default.xex` launch on a modified console.

```powershell
dotnet run --project .\src\X360NetDrive.Server -- `
  --iso-root "U:\Xbox\Games" `
  --urls "http://192.168.1.50:8081"
```

`--iso-root` scans recursively, ignores non-ISO files, and does not follow symbolic links/junctions.
Each ISO becomes a directory under `Games/`, preserving its relative path without the `.iso` suffix.
For example, `Series\Disc 1.iso` is exposed as `Games/Series/Disc 1/default.xex`.
Separate discs and same-named ISOs in different folders remain distinct.
Configure Aurora's scan depth to include the nested game paths.

For a single ISO, the existing `--iso "E:\Games\My Game.iso" --title "My Game"` options remain supported.
An `--iso` directory must contain exactly one immediate ISO. Use exactly one of `--iso` and `--iso-root`;
`--title` applies only to single-ISO mode.

The server's `/` JSON response gives the immutable disk URL, a `games` array with per-disc launch paths,
and an `excluded` array with reasons for any unsupported/unreadable ISOs.
Library mode serves supported entries and logs every exclusion; it refuses startup if none can be exposed.
`/health` reports readiness and included/excluded counts; `/webdav/` supports GET, HEAD, OPTIONS and finite-depth PROPFIND.
Mutations are rejected. Only single byte ranges are supported; there is no authentication.
Restrict the listener/firewall to a trusted LAN and the Pi; never expose this service to the internet.

Set the returned disk URL in the Pi's `x360-netdrive.txt`:

```ini
HTTP_DISK_URL="http://192.168.1.50:8081/exports/<snapshot>/disk"
```

With this option set, the path becomes:

```text
ISO -> C# virtual files/FAT32 -> HTTP ranges -> nbdkit curl -> NBD -> USB
```

This mode exposes one LUN and bypasses rclone, FUSE, the floppy plugin and its COW workaround.
The generated filesystem already has valid FAT32 boot headers and minimum cluster sizing;
no padding file is required. Leave `HTTP_DISK_URL` empty for the existing multi-folder mode.
After adding, removing, or changing ISOs, the title, or export options, disconnect USB, restart the server,
update the snapshot URL, and reconnect the bridge. Do not change the layout under a mounted Xbox.

The `Games/<title>/default.xex` view is suitable for launching through an existing compatible
XBDM plugin. Stock consoles remain subject to normal executable, content and licensing restrictions.
The server does not modify executables or bypass licensing.

Confirm the current USB drive index after reconnecting; Xbox can reassign it. Through an XBDM
client, the launch command is:

```text
magicboot title="USB0:\Games\My Game\default.xex" directory="USB0:\Games\My Game"
```

`xbeinfo running` and `getexecstate` report the current executable and execution state.
The direct path has been verified with a complete 5.3 MiB executable read on a Pi Zero 2 W:
**3.52 MiB/s**, versus approximately **0.21 MiB/s** through the previous FUSE/floppy chain.
These are cold Xbox-file reads, not an in-game loading-time guarantee. The generated FAT32
volume also passed a read-only filesystem check and matched the original ISO's file hash.
Updating a running Pi does not refresh previously saved SD image files; rebuild before reflashing.

## Building an Image with a Local Account

On a Debian/Ubuntu build host, create a private `userconf.txt` outside the repository containing one line:

```text
netdrive:<SHA-512 crypt password hash>
```

Generate the hash interactively with `openssl passwd -6`; do not put a plaintext password in `userconf.txt`. Protect the file with `chmod 600`, then run:

```bash
sudo bash build_image.sh /path/to/userconf.txt
```

If no path is supplied, the builder looks for `config/userconf.txt`. Do not commit credentials or password hashes.
The builder validates and consumes this file inside the image before enabling the read-only root overlay, so the account persists across reboots. The completed image does not retain `userconf.txt` on its boot partition. Keep images with your account private.

## Headless Debugging

Wi-Fi setup runs independently of the USB bridge and retries failed connections. Use a 2.4 GHz network.
To build with SSH and periodic diagnostic snapshots enabled:

```bash
sudo env HEADLESS_DEBUG=1 bash build_image.sh /path/to/userconf.txt
```

Use your configured account and password to connect with `ssh netdrive@<pi-ip>`. Find the address in your router's DHCP list or on HDMI.
Keep this temporary debug image on a trusted LAN; do not forward SSH from the internet.

If Wi-Fi fails, shut down the Pi and return the card to your computer. Read `x360-debug.log` on `bootfs`.
It contains network addresses, service states and recent Wi-Fi/bridge/SSH logs, but does not copy your settings file.
Treat logs as private: they may contain SSIDs and local network details.
The snapshot starts after about 15 seconds and is overwritten every minute, capped at 128 KiB.
Debug logging temporarily writes to the boot partition, so normal power-loss protection does not fully apply.
To stop periodic writes, run `sudo systemctl disable --now x360-netdrive-diagnostics.timer` (with the root overlay this lasts until reboot), or rebuild without `HEADLESS_DEBUG=1`.
