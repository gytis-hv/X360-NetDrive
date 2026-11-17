# Building and testing

Commands start at the repository root unless stated otherwise. The PC server
and Raspberry Pi image are independent builds.

## .NET server

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
The first build restores NuGet packages and needs access to the configured
package sources.

From PowerShell:

```powershell
dotnet build .\server\X360-NetDrive.slnx --configuration Release
dotnet test .\server\X360-NetDrive.slnx --configuration Release --no-build
dotnet publish .\server\src\X360NetDrive.Server --configuration Release
```

The framework-dependent publish is in
`server/src/X360NetDrive.Server/bin/Release/net10.0/publish/`. It requires the
matching .NET 10 ASP.NET Core runtime on the host. Keep the published files
together, including `THIRD_PARTY_NOTICES.md`. For running it, see
[direct ISO setup](SETUP.md#option-b-direct-iso-mode).

### Self-contained single-file server

For example, build the Windows x64 executable from PowerShell:

```powershell
dotnet publish .\server\src\X360NetDrive.Server\X360NetDrive.Server.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false --output .\publish\win-x64
```

Use `linux-x64` for Linux x64. These are self-contained: the host does not need
a separate .NET runtime. Linux still requires compatible system libraries,
including ICU and OpenSSL. The executable may extract native runtime files to a
temporary location at startup. Keep `THIRD_PARTY_NOTICES.md` with the executable
when distributing it. The release workflow packages only the executable and
notices; see [GitHub releases](#github-releases).

## Raspberry Pi image

### Prerequisites

Use a Debian/Ubuntu Linux build host with root access, internet access, enough
free disk space for the downloaded archive and expanded image, loop devices,
mounts, and `systemd-nspawn`. Cross-architecture builds need working ARM user
emulation/binfmt support. WSL2 requires these features to be available in its
environment; they are not guaranteed in every WSL configuration.

The builder lists these host packages:

```bash
sudo apt update
sudo apt install wget xz-utils parted e2fsprogs qemu-user-binfmt systemd-container
```

It downloads the 2024-11-19 Raspberry Pi OS Lite ARMHF image, verifies its
SHA-256, and installs additional packages inside it. Installed package versions
are not pinned, so builds are not reproducible.

### Public image

Run from the `rpi` directory so the builder finds its assets:

```bash
cd rpi
sudo bash build_image.sh
```

The public build removes the base image's first-boot `pi` placeholder account
and rejects unexpected interactive accounts. It locks root and masks
first-boot account creation, SSH and console getty services. It does not accept
an account file. Configure Wi-Fi and server settings on `bootfs` as described
in [Prepare and use](SETUP.md).

The builder caches the base archive in `rpi/build_work/` and writes the raw
`rpi/x360-netdrive-v1.0.img`. A cached archive that fails checksum verification
must be removed and downloaded again. **The output image is overwritten by the
next build.** Back it up first if needed. The builder uses `/mnt/x360_rpi_img`:
do not run concurrent builds or use that mount point for anything else.
The image build needs root and can modify loop devices and mounts; run it only
on a suitable isolated build host.

### Private debug image

For SSH access and periodic diagnostic snapshots, create a private debug image.
It provisions an account; **never publish a debug image or its account file**.
Create a file outside the repository containing exactly one line:

```text
netdrive:<SHA-512-crypt-password-hash>
```

Replace the placeholder with the output of `openssl passwd -6`, which prompts
for a password. Use a non-root username beginning with a lowercase letter,
followed by lowercase letters, digits, or hyphens, at most 32 characters. Keep
the file private (`chmod 600 /path/to/userconf.txt`) and do not commit it.
Do not use a plaintext password.

For example, on the build host, create a private directory and file; substitute
your own username and keep the directory and its contents private:

```bash
mkdir -p "$HOME/x360-private"
chmod 700 "$HOME/x360-private"
umask 077
printf 'netdrive:' > "$HOME/x360-private/userconf.txt"
openssl passwd -6 >> "$HOME/x360-private/userconf.txt"
```

Enter and confirm the password at the prompts. Check that the file contains
one `username:hash` line, without sharing it.

Build from `rpi/`:

```bash
cd rpi
sudo env HEADLESS_DEBUG=1 bash build_image.sh /path/to/userconf.txt
```

For the example above, supply the absolute path to the file in
`$HOME/x360-private/`; the `userconf.txt` path must remain accessible to root.
With no argument, the debug build reads `config/userconf.txt` relative to `rpi/`.
The account is provisioned before the overlay is enabled and the image's copy
of `userconf.txt` is consumed. The debug build overwrites the same output filename
as the public build. Use debug images only on a trusted network.

## Test Pi scripts

On Linux or WSL, from the repository root:

```bash
python3 -m unittest discover -s rpi/tests -p "test_*.py" -v
for script in rpi/build_image.sh rpi/scripts/*.sh; do
    bash -n "$script" || exit
done
```

These checks do not build, flash, or modify an image. A successful build or
test run alone does not establish hardware or release readiness.

## GitHub releases

Pushing a `v*` tag runs the [release workflow](.github/workflows/release.yml).
After tests pass, it builds the public no-login Pi image and single-file server
executables for Windows x64 and Linux x64. Each server executable is smoke-tested
in isolation. If all jobs succeed, a GitHub release is created with:

- `x360-netdrive-rpi-zero2w.img.xz` — compressed raw SD-card image.
- `x360-netdrive-server-win-x64.zip` and `x360-netdrive-server-linux-x64.zip`
  — executable plus server third-party notices.
- `SHA256SUMS.txt` — SHA-256 checksums of those three downloads.

Verify the checksums after download. For the image, decompress the `.xz` to
obtain the `.img` before flashing, or use an imager that supports `.xz`.
Use an unmodified trusted image and check
[setup instructions](SETUP.md#2-flash-the-pi-image).
The workflow does not publish private debug images. A successful workflow is
not a substitute for testing the image on hardware or reviewing third-party
license obligations, including Raspberry Pi OS package obligations.
