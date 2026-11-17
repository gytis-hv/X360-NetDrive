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

It downloads the 2024-11-19 Raspberry Pi OS Lite ARMHF image and installs
additional packages inside it. The download and package versions are not fully
pinned or verified by the current builder; do not treat the output as a
reproducible or independently verified release.

**The current builder always provisions a local account**, even when SSH is
disabled. Images built with your credentials are private: do not publish them.
A public no-login image build is not implemented yet.

### Create private account input

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

### Build

Run from the `rpi` directory so the builder finds its assets:

```bash
cd rpi
sudo bash build_image.sh /path/to/userconf.txt
```

For the example above, supply the absolute path to the file in
`$HOME/x360-private/`; the `userconf.txt` path must remain accessible to root.
With no argument, it reads `config/userconf.txt` relative to `rpi/`.
The builder caches the base archive in `rpi/build_work/` and writes
`rpi/x360-netdrive-v1.0.img`. **It overwrites that output image on the next
build.** Move or back up an image you want to keep before rebuilding.
It also uses `/mnt/x360_rpi_img` as its mount point; do not run it concurrently
with another build or against a mount point in use.

The account is provisioned before the read-only overlay is enabled; the input
`userconf.txt` is consumed inside the image. SSH is disabled by default, but
the local account remains. The output is a raw `.img`, not an `.img.xz`.
Flash it as described in [Prepare and use](SETUP.md).

For a **private debug image** with SSH and periodic diagnostic snapshots:

```bash
cd rpi
sudo env HEADLESS_DEBUG=1 bash build_image.sh /path/to/userconf.txt
```

This writes the same output filename as the normal build. Keep the image and
its credentials private and use it only on a trusted network.

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
