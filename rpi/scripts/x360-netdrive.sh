#!/bin/bash
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/boot/firmware/x360-netdrive.txt}"
STATE_DIR="${STATE_DIR:-/run/x360-netdrive}"

# 1. Load user config safely (strip Windows CRLF line endings)
if [ -f "$CONFIG_FILE" ]; then
    CONFIG_ENV=$(mktemp)
    trap 'rm -f "$CONFIG_ENV"' EXIT
    sed 's/\r$//' "$CONFIG_FILE" > "$CONFIG_ENV"
    # shellcheck disable=SC1091
    source "$CONFIG_ENV"
    rm -f "$CONFIG_ENV"
    trap - EXIT
fi

if [ "${1:-}" = "--wifi-only" ]; then
    if [ -z "${WIFI_SSID:-}" ] || [ "$WIFI_SSID" = "Your_WiFi_Name" ]; then
        echo "[X360-NetDrive] Configure WIFI_SSID in $CONFIG_FILE." >&2
        exit 1
    fi
    raspi-config nonint do_wifi_country "${WIFI_COUNTRY:-US}"
    rfkill unblock wifi
    nmcli radio wifi on
    if nmcli -g NAME connection show | grep -Fx "X360NetWiFi" >/dev/null; then
        nmcli connection delete "X360NetWiFi"
    fi
    nmcli --wait 30 device wifi connect "$WIFI_SSID" password "${WIFI_PASSWORD:-}" name "X360NetWiFi"
    echo "[X360-NetDrive] Wi-Fi connected."
    ip -br address
    exit 0
fi

usb_detach() {
    if [ -d /sys/module/g_mass_storage ]; then
        modprobe -r g_mass_storage
    fi
}

usb_attach() {
    if [ ! -s "$STATE_DIR/devices" ]; then
        echo "[X360-NetDrive] Connect a source before attaching USB." >&2
        return 1
    fi
    if [ -d /sys/module/g_mass_storage ]; then
        echo "[X360-NetDrive] USB is already attached."
        return
    fi
    mapfile -t devices < "$STATE_DIR/devices"
    if [ "${#devices[@]}" -ne 1 ]; then
        echo "[X360-NetDrive] Exactly one source is required for the fixed USB drive." >&2
        return 1
    fi
    NBD_DEVICES="${devices[0]}"
    USB_SERIAL=$(tr -d '\0' < "${USB_SERIAL_FILE:-/sys/firmware/devicetree/base/serial-number}")
    if [ -z "$USB_SERIAL" ]; then
        echo "[X360-NetDrive] Cannot determine the Pi USB serial number." >&2
        return 1
    fi
    modprobe g_mass_storage file="$NBD_DEVICES" stall=0 \
        removable=1 ro=1 \
        idVendor=0x0781 idProduct=0x5575 iManufacturer="SanDisk" \
        iProduct="X360-NetDrive" iSerialNumber="$USB_SERIAL"
}

source_disconnect() {
    usb_detach
    if [ -f "$STATE_DIR/devices" ]; then
        while IFS= read -r dev; do
            status=0
            nbd-client -c "$dev" >/dev/null 2>&1 || status=$?
            case "$status" in
                0) nbd-client -d "$dev" ;;
                1) ;;
                *) echo "[X360-NetDrive] Cannot check NBD connection: $dev" >&2; return "$status" ;;
            esac
        done < "$STATE_DIR/devices"
    fi
    for i in {1..8}; do
        if mountpoint -q "/mnt/lun$i"; then
            umount "/mnt/lun$i"
        fi
    done
    rm -f "$STATE_DIR/devices" "$STATE_DIR/usb-files"
    rm -f "$STATE_DIR/nbd1-raw.sock"
}

case "${1:-}" in
    --usb-attach) usb_attach; exit ;;
    --usb-detach) usb_detach; exit ;;
    --disconnect) source_disconnect; exit ;;
    ""|--source-only) ;;
    *) echo "[X360-NetDrive] Unknown action: $1" >&2; exit 1 ;;
esac

if [ -f "$STATE_DIR/source.conf" ]; then
    # Written only by the authenticated controller with shell-quoted values.
    source "$STATE_DIR/source.conf"
fi
SERVER_URL="${SERVER_URL:-}"
HTTP_DISK_URL="${HTTP_DISK_URL:-}"
if [ "${MANAGED_SLOTS:-0}" = 1 ]; then
    HTTP_DISK_URL=""
else
    if [ -n "$HTTP_DISK_URL" ]; then
        server_address="$HTTP_DISK_URL"
    else
        server_address="$SERVER_URL"
    fi
    if [ -z "$server_address" ]; then
        echo "[X360-NetDrive] Configure SERVER_URL with an X360 Net Server base URL or select a server in the control page." >&2
        exit 1
    fi
    HTTP_DISK_URL=$("${CONTROL_BIN:-/usr/local/bin/x360-netdrive-control}" --discover-server "$server_address")
fi
install -d -m 700 "$STATE_DIR"
if [ -e "$STATE_DIR/devices" ]; then
    echo "[X360-NetDrive] Source state already exists; disconnect before connecting." >&2
    exit 1
fi
# Daemons belong to the source systemd unit; systemd terminates them on failure/stop.
for i in {1..8}; do
    if [ -S "/tmp/nbd$i.sock" ]; then
        rm -- "/tmp/nbd$i.sock"
    fi
done

# 4. Use only LUN0: the tested Xbox does not enumerate additional LUNs.
wait_for_server() {
    echo "[X360-NetDrive] Waiting for server at $1..."
    until curl -s --head --fail --connect-timeout 3 --max-time 10 "$1" >/dev/null; do
        sleep 2
    done
}
LUN_LIST=()
if [ "${MANAGED_SLOTS:-0}" = 1 ]; then
    for idx in "${!SLOT_URLS[@]}"; do
        if [ -n "${SLOT_URLS[$idx]:-}" ]; then
            LUN_LIST+=("$idx")
        fi
    done
else
    if [ "${LUN_COUNT:-auto}" != auto ] && [ "${LUN_COUNT:-auto}" != 1 ]; then
        echo "[X360-NetDrive] Only one USB drive is supported; set LUN_COUNT=1 or use the control page." >&2
        exit 1
    fi
    LUN_LIST=(1)
fi
if [ "${#LUN_LIST[@]}" -gt 1 ] || { [ "${#LUN_LIST[@]}" -eq 1 ] && [ "${LUN_LIST[0]}" != 1 ]; }; then
    echo "[X360-NetDrive] Invalid fixed-drive mapping; reconnect a single server from the control page." >&2
    exit 1
fi
if [ "${MANAGED_SLOTS:-0}" = 1 ] && [ "${#LUN_LIST[@]}" -eq 1 ]; then
    case "${SLOT_TYPES[1]:-}" in
        disk) HTTP_DISK_URL="${SLOT_URLS[1]}" ;;
        webdav) echo "[X360-NetDrive] WebDAV sources are no longer supported; select an X360 Net Server." >&2; exit 1 ;;
        *) echo "[X360-NetDrive] Invalid source type in USB1." >&2; exit 1 ;;
    esac
fi

echo "[X360-NetDrive] Mounting LUNs: ${LUN_LIST[*]}"

# Initialize the NBD disk export.
USB_DISK_BYTES=2199023255040
NBD_SIZE_HELPER="${NBD_SIZE_HELPER:-/usr/local/bin/x360-nbd-size.py}"
modprobe nbd max_part=8 nbds_max=8
NBD_DEVICES=""
: > "$STATE_DIR/devices"

for i in "${LUN_LIST[@]}"; do
    wait_for_server "$HTTP_DISK_URL"
    SOCK="/tmp/nbd${i}.sock"
    RAW_SOCK="$STATE_DIR/nbd${i}-raw.sock"
    DEV="/dev/nbd0"
    rm -f "$RAW_SOCK"
    printf '%s\n' "$DEV" >> "$STATE_DIR/devices"

    nbdkit -r -U "$RAW_SOCK" curl "$HTTP_DISK_URL" \
        protocols=http,https timeout=30 tcp-keepalive=true tcp-nodelay=true

    "$NBD_SIZE_HELPER" "$RAW_SOCK" "$USB_DISK_BYTES"

    # Keep the raw export alive so the size check and padded view use the same snapshot.
    nbdkit --filter=truncate -U "$SOCK" nbd socket="$RAW_SOCK" truncate="$USB_DISK_BYTES"
    nbd-client -unix "$SOCK" "$DEV"
    if [ -z "$NBD_DEVICES" ]; then
        NBD_DEVICES="$DEV"
    else
        NBD_DEVICES="${NBD_DEVICES},${DEV}"
    fi

done

if [ "${1:-}" != --source-only ]; then
    usb_attach
fi
echo "[X360-NetDrive] Source connected on $NBD_DEVICES"
