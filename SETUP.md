# Prepare and use

This guide covers X360 Net Server and its ISO library. You need a
Raspberry Pi Zero 2 W, a microSD card of at least 8 GB, a data-capable micro-USB
cable, a 2.4 GHz Wi-Fi network, and a host that remains available while using
the console. Use only content you are entitled to use.

The bridge presents read-only storage. It does not sign, modify, or license
games; stock consoles remain subject to their normal content restrictions.
The server has no authentication. Restrict
access to a trusted LAN and firewall; do not expose them to the internet.

## 1. Prepare the content host

Configure the host firewall to allow the Pi to reach the server, and keep
the server running. Generic WebDAV folders are not supported.

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

For a GitHub release, extract the server archive for your host. It contains a
self-contained executable and its required third-party notices; .NET does not
need to be installed separately. Linux hosts still need compatible system
libraries, including ICU and OpenSSL. For example, on Windows:

```powershell
.\X360NetDrive.Server.exe --iso-root "D:\Xbox360\ISOs" --urls "http://192.168.1.50:8081"
```

On Linux, from the extracted archive directory:

```bash
chmod +x ./X360NetDrive.Server
./X360NetDrive.Server --iso-root "/path/to/ISOs" --urls "http://192.168.1.50:8081"
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
`warnings`, and a `disk` path such as `/exports/<snapshot>/disk`. Enter the
server base URL in step 3, not the snapshot path. Check `excluded` and
`warnings` before connecting the Pi.

The server exposes one disk using HTTP byte ranges. No padding file is needed. It reads ISO
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

Build a public image using [Building and testing](BUILDING.md), or download a
release and verify its SHA-256 using `SHA256SUMS.txt`. Decompress `.img.xz`
to `.img` if your imager does not support `.xz`. Public images have no
interactive login or SSH; a separate private debug build can enable them.
Never publish debug images containing your account or distribute credentials.

Flash the `.img` using Raspberry Pi Imager or balenaEtcher. Disable Raspberry
Pi Imager's OS customization: the image is already configured. Flashing erases
the selected card, so verify the target first. Reinsert the card and open its
`bootfs` partition.

## 3. Configure Wi-Fi and the server

Edit `x360-netdrive.txt` on `bootfs`:

```ini
WIFI_SSID="Your_2.4GHz_Network"
WIFI_PASSWORD="Your_WiFi_Password"
WIFI_COUNTRY="US"
SERVER_URL="http://192.168.1.50:8081"
```

Use your actual country code, host address, and port. The file is sourced as Bash, not parsed as plain INI:
edit only trusted files, and escape shell-special characters in quoted values
(for example `\$`, `\"`, and `\\` inside double quotes; put a backslash before
any backtick). Do not share the configured file: it contains your Wi-Fi password.
Save it and safely eject the card.

### Enable web control

New images include a lightweight control page on port **8080** of the Pi.
Before booting, put a unique random admin token in `x360-admin-token.txt`
on `bootfs` (one line, 32-128 characters: letters, digits, `_` or `-`).
For example, `openssl rand -hex 32` generates a suitable token; save its output
privately in that file. Do not reuse tokens or share the configured card.
The distributed image contains only an empty token file, never a shared secret.

Open `http://<Pi-IP>:8080/` using the address from your router. Use the HTML sign-in form with
username **admin** and the token as the password. If the file is absent, empty,
or invalid, the control service refuses to start; automatic bridging still works.
After changing the token on the card, reboot the Pi.
Browser sessions expire after 12 hours, when you click **Sign out**, or when the
control service restarts. Signing out does not detach USB drives.

The page can:

- Show source/USB service status and recent bridge logs.
- Save named servers, including ones currently offline.
- Mount a saved server on the fixed USB drive, replacing the current source.
- Unmount the selected server without deleting saved profiles.
- Temporarily attach or detach the USB drive.

Enter the X360 Net Server **base URL** (for example
`http://192.168.1.50:8081/`), not its snapshot disk URL. Profiles do not
currently support servers requiring credentials.

Profiles and the selected server are saved in `x360-netdrive/servers.json` on
`bootfs` and restored automatically at boot. Once this file exists, it takes
precedence over the source settings in `x360-netdrive.txt` (Wi-Fi settings remain
there). Saving the first profile does not interrupt an existing legacy
connection, but the next reconnect or reboot uses the saved selection.
No selected server means no USB media at boot. Back up this file along
with your configuration; do not edit it while the control service is running.

Choose a saved server and click **Mount server**. This switches the source behind
one fixed read-only USB drive, always on LUN0 with the same gadget serial number.
The tested Xbox ignores additional LUNs, so slot choices and simultaneous
servers are not supported. The Xbox still assigns the external drive name;
other connected USB devices can affect that name. Keep the same folder layout
on each server to reuse Aurora's scan path, and rescan after changing libraries.
Unmount a selected server before deleting its profile.

The USB disk always reports 2 TiB minus one 512-byte sector
(2,199,023,255,040 bytes). The Pi pads smaller source disks with zeroes without
changing their partitions or increasing their usable filesystem space, and
rejects larger or non-sector-aligned sources instead of truncating them.
This fixed capacity may help Aurora recognize a switched drive, but other
identity factors have not been ruled out. Existing Aurora scan paths may need
updating once after changing from the previous variable-size disk.

**Switching servers briefly reconnects the USB drive.** Stop every game and
return to the dashboard first; the page requires confirmation. Temporary
detachment preserves the selection and leaves the source connected; to stay
disconnected after reboot, use **Unmount server** instead.

The settings file retains its version-1 format for compatibility. A sole older
slot assignment is read as the fixed drive. Multiple older assignments produce
an explicit error; mounting any saved server in the page replaces them with
one selection, without deleting profiles.

Refresh status after a request: `activating` means connection is still in
progress, not success. The selected source must connect successfully before USB
attaches. Restore server/network availability, then use **Reconnect and attach** to retry.
An invalid settings file is reported as an error, never silently replaced or
ignored. A failed operation may leave USB detached; consult the status and logs.

Existing WebDAV profiles are retained but cannot be mounted. Add and select an
X360 Net Server profile instead. For card-only upgrades, replace an old WebDAV
`SERVER_URL` with the server base URL. A legacy `HTTP_DISK_URL` pointing to an
X360 Net Server snapshot is resolved through that server's current manifest;
remove it when switching to `SERVER_URL`, because it takes precedence.

Reconnecting discovers fresh snapshot URLs, but does not rescan the PC ISO
server. Restart that server after adding ISOs, then reconnect the selected server.

The control page uses HttpOnly, SameSite session cookies and anti-CSRF checks.
HTTP Basic headers remain supported for command-line clients without triggering
a browser login popup. Plain HTTP does **not** encrypt the token, session cookie,
or traffic. Use only a trusted LAN, or put it
behind a trusted HTTPS reverse proxy that preserves the public `Host` header.
Never forward port 8080 to the internet.
Logs can contain source URLs and local network details; keep access private.
The token enables web control, not an interactive Linux login.

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

Update the ISOs or server options, restart the server, then reconnect the
selected server in the control page. Card-only setups discover the new snapshot
when the bridge restarts.

The simplest way to change the Pi configuration is to power it down after
disconnecting, edit `x360-netdrive.txt` on the card's `bootfs`, safely eject
the card, and power it back on. Avoid restarting the bridge while the Xbox is
actively reading it.

## Troubleshooting

- **No Wi-Fi:** check 2.4 GHz availability, country code, credentials, and
  Bash escaping. Wi-Fi setup retries independently of the USB bridge.
- **No drive:** check the host server is running and reachable from the Pi,
  the configured server URL, the data cable and Pi USB port, and the Xbox's
  current USB drive index. Inspect `/health`, `excluded`, and `warnings`.
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
