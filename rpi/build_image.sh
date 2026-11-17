#!/bin/bash
set -euo pipefail

# Build script for X360-NetDrive Raspberry Pi Zero 2 W distributable image
# Run as root (sudo ./build_image.sh /path/to/userconf.txt) on Debian/Ubuntu/WSL2 with:
#   apt install -y wget xz-utils parted e2fsprogs qemu-user-binfmt systemd-container

IMAGE_URL="https://downloads.raspberrypi.com/raspios_lite_armhf/images/raspios_lite_armhf-2024-11-19/2024-11-19-raspios-bookworm-armhf-lite.img.xz"
WORK_DIR="./build_work"
OUTPUT_IMG="x360-netdrive-v1.0.img"
MOUNT_DIR="/mnt/x360_rpi_img"
LOOP_DEV=""
USERCONF_FILE="${1:-config/userconf.txt}"
HEADLESS_DEBUG="${HEADLESS_DEBUG:-0}"

cleanup() {
    echo "[Cleanup] Unmounting filesystems and detaching loop devices..."
    umount "${MOUNT_DIR}/boot/firmware" 2>/dev/null || true
    umount "${MOUNT_DIR}" 2>/dev/null || true
    if [ -n "$LOOP_DEV" ]; then
        losetup -d "$LOOP_DEV" 2>/dev/null || true
    fi
}
trap cleanup EXIT

if [ "$EUID" -ne 0 ]; then
    echo "Please run as root (sudo ./build_image.sh /path/to/userconf.txt)"
    exit 1
fi
if [[ "$HEADLESS_DEBUG" != 0 && "$HEADLESS_DEBUG" != 1 ]]; then
    echo "HEADLESS_DEBUG must be 0 or 1." >&2
    exit 1
fi

if [ ! -f "$USERCONF_FILE" ]; then
    echo "Missing userconf.txt: supply a file containing username:SHA-512-crypt-hash." >&2
    exit 1
fi
USERCONF=$(sed 's/\r$//' "$USERCONF_FILE")
USER_NAME="${USERCONF%%:*}"
USER_HASH="${USERCONF#*:}"
if [[ ! "$USER_NAME" =~ ^[a-z][a-z0-9-]{0,31}$ ]] || [ "$USER_NAME" = root ] ||
    [[ ! "$USER_HASH" =~ ^\$6\$(rounds=[0-9]+\$)?[./a-zA-Z0-9]{1,16}\$[./a-zA-Z0-9]{86}$ ]] ||
    [ "$USERCONF" != "$USER_NAME:$USER_HASH" ]; then
    echo "Invalid userconf.txt: use one line with a non-root username and SHA-512 crypt hash." >&2
    exit 1
fi

mkdir -p "$WORK_DIR" "$MOUNT_DIR"

ARCHIVE_PATH="${WORK_DIR}/base.img.xz"
if [ ! -f "$ARCHIVE_PATH" ]; then
    echo "[1/7] Downloading base Raspberry Pi OS Lite image..."
    wget -O "$ARCHIVE_PATH" "$IMAGE_URL"
fi

echo "[2/7] Extracting and expanding base image (+1500MB)..."
xz -dkc "$ARCHIVE_PATH" > "$OUTPUT_IMG"
truncate -s +1500M "$OUTPUT_IMG"
parted "$OUTPUT_IMG" --script resizepart 2 100%

LOOP_DEV=$(losetup -Pf --show "$OUTPUT_IMG")
e2fsck -fy "${LOOP_DEV}p2"
resize2fs "${LOOP_DEV}p2"

echo "[3/7] Mounting image partitions..."
mount "${LOOP_DEV}p2" "$MOUNT_DIR"
mount "${LOOP_DEV}p1" "${MOUNT_DIR}/boot/firmware"

echo "[4/7] Configuring kernel overlays, modules, and default user config..."
grep -qxF "dtoverlay=dwc2" "${MOUNT_DIR}/boot/firmware/config.txt" || echo "dtoverlay=dwc2" >> "${MOUNT_DIR}/boot/firmware/config.txt"
grep -qxF "dwc2" "${MOUNT_DIR}/etc/modules" || echo "dwc2" >> "${MOUNT_DIR}/etc/modules"
grep -qxF "nbd" "${MOUNT_DIR}/etc/modules" || echo "nbd" >> "${MOUNT_DIR}/etc/modules"
printf '%s\n' "$USERCONF" > "${MOUNT_DIR}/boot/firmware/userconf.txt"
systemd-nspawn -D "$MOUNT_DIR" /bin/bash -e -c '
    IFS=: read -r expected_user expected_hash < /boot/firmware/userconf.txt
    /usr/lib/userconf-pi/userconf-service
    test "$(getent passwd 1000 | cut -d: -f1)" = "$expected_user"
    test "$(getent shadow "$expected_user" | cut -d: -f2)" = "$expected_hash"
    test "$(systemctl is-enabled userconfig.service)" = disabled
    test ! -f /boot/firmware/userconf.txt
'

cp config/x360-netdrive.txt "${MOUNT_DIR}/boot/firmware/x360-netdrive.txt"
cp scripts/x360-netdrive.sh "${MOUNT_DIR}/usr/local/bin/x360-netdrive.sh"
chmod +x "${MOUNT_DIR}/usr/local/bin/x360-netdrive.sh"
install -m 755 scripts/x360-fat32.py "${MOUNT_DIR}/usr/local/bin/"
cp systemd/x360-netdrive.service "${MOUNT_DIR}/etc/systemd/system/x360-netdrive.service"
install -m 644 systemd/x360-netdrive-wifi.service "${MOUNT_DIR}/etc/systemd/system/"
if [ "$HEADLESS_DEBUG" = 1 ]; then
    install -m 755 scripts/x360-netdrive-diagnostics.sh "${MOUNT_DIR}/usr/local/bin/"
    install -m 644 systemd/x360-netdrive-diagnostics.service systemd/x360-netdrive-diagnostics.timer \
        "${MOUNT_DIR}/etc/systemd/system/"
    install -m 644 config/sshd-headless.conf "${MOUNT_DIR}/etc/ssh/sshd_config.d/20-x360-headless.conf"
    sed -i 's/ quiet//g' "${MOUNT_DIR}/boot/firmware/cmdline.txt"
    systemctl --root="$MOUNT_DIR" enable ssh.service x360-netdrive-diagnostics.timer
fi

echo "[5/7] Installing packages and enabling services inside systemd-nspawn..."
systemd-nspawn -D "$MOUNT_DIR" /bin/bash -e -c "
    export DEBIAN_FRONTEND=noninteractive
    sed -i 's/^MODULES=.*/MODULES=most/' /etc/initramfs-tools/initramfs.conf
    apt-get update
    apt-get install -y --no-install-recommends rclone nbdkit nbd-client curl zram-tools python3

    cat << 'EOF' > /etc/default/zramswap
ALGO=lz4
PERCENT=50
PRIORITY=100
EOF

    systemctl enable zramswap.service
    systemctl enable x360-netdrive.service
    systemctl enable x360-netdrive-wifi.service
"

echo "[6/7] Enabling read-only overlayfs for power-loss protection..."
systemd-nspawn -D "$MOUNT_DIR" /bin/bash -e -c "
    raspi-config nonint enable_overlayfs
    apt-get clean
    rm -rf /var/lib/apt/lists/*
"

echo "[7/7] Finalizing image..."
sync
cleanup
trap - EXIT

echo "Build complete: ${OUTPUT_IMG}"
