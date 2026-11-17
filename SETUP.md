# Prepare and use

This guide covers both WebDAV folder mode and direct ISO mode. You need a
Raspberry Pi Zero 2 W, a microSD card of at least 8 GB, a data-capable micro-USB
cable, a 2.4 GHz Wi-Fi network, and a host that remains available while using
the console. Use only content you are entitled to use.

The bridge presents read-only storage. It does not sign, modify, or license
games; stock consoles remain subject to their normal content restrictions.
The ISO server and example WebDAV server have no authentication. Restrict
access to a trusted LAN and firewall; do not expose them to the internet.

## 1. Prepare the content host

Choose **one** mode. In either mode, configure the host firewall to allow the
Pi to reach the selected address and port, and keep the server running.

### Option A: WebDAV folder mode

Create a directory on your host containing `Xbox_LUN1` through `Xbox_LUN8`,
as needed. Put extracted game files or GOD containers inside these directories.
For example:

```text
D:\Xbox360\
  Xbox_LUN1\
    My Game\default.xex
  Xbox_LUN2\
```

Each LUN becomes a separate virtual drive. FAT32 files must be smaller than
4 GiB; the maximum LUN size is 2 TiB. The Pi constructs the FAT table in RAM;
the existing guidance is to keep each share around 250-400 GB, but the
practical limit depends on file count and layout. Very small shares may need a
1 GiB sparse padding file because FAT32 requires at least 65,525 clusters.
The bridge validates this and rejects a layout that is too small.

For example, serve the parent directory read-only using
[rclone](https://rclone.org/) on Windows:

```powershell
rclone serve webdav D:\Xbox360 --addr 192.168.1.50:8080 --read-only
```

Replace the address with the host's LAN IP. Leave the command running. Check
from another device that `http://192.168.1.50:8080/` is reachable.
The Pi's configuration has no WebDAV username/password fields; use a server
accessible to the Pi without interactive authentication on a trusted network.
Set `SERVER_URL` to that base URL and leave `HTTP_DISK_URL` empty in step 3.

With `LUN_COUNT=auto`, the bridge searches for `Xbox_LUN1` through `Xbox_LUN8`;
if none are detected it falls back to `Xbox_LUN1`. Alternatively specify a
number from 1 to 8, which uses the corresponding consecutive LUN directories.
The bridge repairs FAT32 boot headers using a local copy-on-write overlay;
Xbox access remains read-only.

### Option B: Direct ISO mode

Build or obtain the .NET 10 server; see [Building and testing](BUILDING.md).
The following starts the source project from the repository root on Windows:

```powershell
dotnet run --project .\server\src\X360NetDrive.Server -- `
  --iso-root "D:\Xbox360\ISOs" `
  --urls "http://192.168.1.50:8081"
```

Alternatively, run a framework-dependent published server from the repository
root, with the .NET 10 ASP.NET Core runtime installed:

```powershell
dotnet .\server\src\X360NetDrive.Server\bin\Release\net10.0\publish\X360NetDrive.Server.dll `
  --iso-root "D:\Xbox360\ISOs" `
  --urls "http://192.168.1.50:8081"
```

Replace the ISO path and host LAN address. Keep the process running. The host
must have access to the ISO files and the Pi must be able to reach the port.
The server scans recursively for `.iso` files, ignores other files and does
not follow symbolic links or junctions. Unsupported or locked ISOs are
excluded and reported; startup fails if none can be exposed.

For one ISO, use `--iso "D:\Xbox360\Game.iso"` instead of `--iso-root`.
`--iso` can also name a directory containing exactly one immediate ISO.
Use only one of `--iso` and `--iso-root`; an optional `--title "My Game"`
overrides the title in single-ISO mode.

Open `http://192.168.1.50:8081/health` and the root URL
`http://192.168.1.50:8081/`. The root response contains `games`, `excluded`,
`warnings`, and a `disk` path such as `/exports/<snapshot>/disk`. Combine that
path with the host address and enter the **complete** URL as `HTTP_DISK_URL`
in step 3. Check `excluded` and `warnings` before connecting the Pi.

This mode exposes one LUN using HTTP byte ranges, bypassing the Pi's folder-mode
rclone/FUSE and FAT32 overlay. No padding file is needed. The server reads ISO
files without modifying them or creating a full extracted or disk-image copy.
Keep the ISOs and the running server unchanged while the Xbox has the disk.

By default, content appears under `Games/ISOs/` using locally read XEX/XDBF
titles and available title, disc, and media identifiers. Missing or unsupported
metadata falls back to the filename with a warning; FAT-unsafe characters and
duplicate names are handled and reported. `--iso-metadata-layout false` instead
uses the source-relative hierarchy under `Games/`. For a compatible launcher,
configure its scan path and depth for the selected layout.

Title extraction supports plaintext and AES-CBC-encrypted XEX resources in
uncompressed, basic-compressed, or normal LZX-compressed images. LZX input is
SHA-1 checked and bounded; see the [server notices](server/THIRD_PARTY_NOTICES.md)
for decoder attribution. Exported executable bytes are not changed.

`--god true` additionally creates an **experimental**, unsigned and unlicensed
GOD/SVOD projection; successful direct XEX launch does not prove GOD dashboard
launch works. This option scans the image and stores hash metadata in the
server cache (about 36 MiB for a 7.5 GB partition). `--cache <directory>`
changes its location. Graceful shutdown removes that run's metadata; forced
termination can leave it behind.

## 2. Flash the Pi image

Build a private image using [Building and testing](BUILDING.md), or use an
image whose provenance and account configuration you trust. The current
builder creates a **local account in every image**; SSH is disabled by default
and enabled only in the debug build. Do not publish images containing your
account or distribute credentials.

Flash the `.img` using Raspberry Pi Imager or balenaEtcher. Disable Raspberry
Pi Imager's OS customization: the image is already configured. Flashing erases
the selected card, so verify the target first. Reinsert the card and open its
`bootfs` partition.

## 3. Configure Wi-Fi and the selected mode

Edit `x360-netdrive.txt` on `bootfs`. For folder mode:

```ini
WIFI_SSID="Your_2.4GHz_Network"
WIFI_PASSWORD="Your_WiFi_Password"
WIFI_COUNTRY="US"
SERVER_URL="http://192.168.1.50:8080"
HTTP_DISK_URL=""
LUN_COUNT=auto
```

For direct ISO mode, set the Wi-Fi fields and use the exact URL returned by
the server:

```ini
HTTP_DISK_URL="http://192.168.1.50:8081/exports/<snapshot>/disk"
```

A nonempty `HTTP_DISK_URL` selects direct ISO mode and ignores `LUN_COUNT`;
clear it to return to folder mode. Use your actual country code, host address,
port, and snapshot. The file is sourced as Bash, not parsed as plain INI:
edit only trusted files, and escape shell-special characters in quoted values
(for example `\$`, `\"`, and `\\` inside double quotes; put a backslash before
any backtick). Do not share the configured file: it contains your Wi-Fi password.
Save it and safely eject the card.

## 4. Connect and use

1. Insert the card in the Pi. Connect its inner **USB data** port (not `PWR IN`)
   to an Xbox USB port with a data-capable cable.
2. Do **not** simultaneously connect a separate power supply to `PWR IN`:
   this can cause power backfeed.
3. Power on the Xbox. Wait for Wi-Fi and the bridge; availability depends on
   network and host startup. The Xbox assigns the USB drive index, which may
   change after reconnecting.
4. Browse the USB drive using a compatible dashboard/launcher. Direct XEX
   launch was tested on a modified Xbox with XBDM; the bridge does not override
   stock-console restrictions.

For an XBDM client, the server manifest's `games[].launchPath` identifies the
virtual executable. After confirming the Xbox's current USB index, an example
command is:

```text
magicboot title="USB0:\Games\ISOs\My Game [12345678][9ABCDEF0]\default.xex" directory="USB0:\Games\ISOs\My Game [12345678][9ABCDEF0]"
```

`xbeinfo running` and `getexecstate` can help confirm which executable is
running; acceptance of a launch command alone does not prove a game started.

The image uses a read-only root overlay to reduce SD-card write exposure, but
it is not a guarantee against corruption or data loss. Shut down cleanly when
possible, keep backups, and do not disconnect during transfers. Debug snapshots
write to the boot partition and weaken this protection.

## Updating content

Do not change source files or the virtual layout while the Xbox is using it.
Stop using the disk and disconnect the bridge first.

- **Folder mode:** update the WebDAV folders, then restart the Pi/bridge to
  rebuild its virtual layout.
- **Direct ISO mode:** update the ISOs or server options, restart the server,
  inspect its new manifest, update `HTTP_DISK_URL` if the snapshot changed, and
  reconnect the Pi/bridge. Do not assume the previous snapshot URL is valid.

The simplest way to change the Pi configuration is to power it down after
disconnecting, edit `x360-netdrive.txt` on the card's `bootfs`, safely eject
the card, and power it back on. Avoid restarting the bridge while the Xbox is
actively reading it.

## Troubleshooting

- **No Wi-Fi:** check 2.4 GHz availability, country code, credentials, and
  Bash escaping. Wi-Fi setup retries independently of the USB bridge.
- **No drive:** check the host server is running and reachable from the Pi,
  the configured URL and mode, the data cable and Pi USB port, and the Xbox's
  current USB drive index. In folder mode check the LUN names and FAT32 size
  warning; in ISO mode inspect `/health`, `excluded`, and `warnings`.
- **Missing or incorrectly named ISO:** inspect the server's manifest and logs;
  locked or unsupported images are excluded, and title fallback is reported.
- **Game does not start:** USB enumeration is not proof of game compatibility.
  Check console/content restrictions and the launcher configuration. GOD/SVOD
  projection remains experimental.

For deeper diagnosis, build the private `HEADLESS_DEBUG=1` image described in
[Building and testing](BUILDING.md). It enables SSH and writes
`bootfs/x360-debug.log` about every minute after startup, capped at 128 KiB.
Find the Pi's IP in your router or on HDMI and use the account created during
the build. The log can contain SSIDs and local network details; treat it as
private. It does not copy the settings file. Do not expose SSH to the internet.
To stop periodic snapshots temporarily, use
`sudo systemctl disable --now x360-netdrive-diagnostics.timer`; with the root
overlay this lasts until reboot. Rebuild without debug mode to disable it in
the image.
