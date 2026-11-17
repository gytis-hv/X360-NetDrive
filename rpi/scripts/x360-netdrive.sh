#!/bin/bash
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/boot/firmware/x360-netdrive.txt}"

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

SERVER_URL="${SERVER_URL:-http://192.168.1.50:8080}"
SERVER_URL="${SERVER_URL%/}"
HTTP_DISK_URL="${HTTP_DISK_URL:-}"
if [ -n "$HTTP_DISK_URL" ] && [[ ! "$HTTP_DISK_URL" =~ ^https?:// ]]; then
    echo "[X360-NetDrive] HTTP_DISK_URL must be an HTTP(S) URL." >&2
    exit 1
fi
PROBE_URL="${HTTP_DISK_URL:-$SERVER_URL}"

# 3. Wait until WebDAV server is reachable
echo "[X360-NetDrive] Waiting for server at $PROBE_URL..."
until curl -s --head --fail --connect-timeout 3 --max-time 10 "$PROBE_URL" >/dev/null; do
    sleep 2
done

# 4. Determine LUN list (auto-detect Xbox_LUN1..8 or use explicit count)
LUN_LIST=()
if [ -n "$HTTP_DISK_URL" ]; then
    LUN_LIST=(1)
elif [ "${LUN_COUNT:-auto}" = "auto" ]; then
    AVAILABLE_DIRS=$(rclone lsd :webdav: --webdav-url "$SERVER_URL" 2>/dev/null | awk '{print $NF}' || true)
    for idx in $(seq 1 8); do
        if echo "$AVAILABLE_DIRS" | grep -qx "Xbox_LUN${idx}"; then
            LUN_LIST+=("$idx")
        fi
    done
    # Fallback to LUN 1 if auto-detection finds nothing
    if [ ${#LUN_LIST[@]} -eq 0 ]; then
        LUN_LIST=(1)
    fi
else
    COUNT=${LUN_COUNT:-2}
    if [ "$COUNT" -lt 1 ]; then COUNT=1; fi
    if [ "$COUNT" -gt 8 ]; then COUNT=8; fi
    for idx in $(seq 1 "$COUNT"); do
        LUN_LIST+=("$idx")
    done
fi

echo "[X360-NetDrive] Mounting LUNs: ${LUN_LIST[*]}"

# 5. Initialize NBD devices & mount WebDAV LUNs
modprobe nbd max_part=8 nbds_max=8
NBD_DEVICES=""
NBD_INDEX=0

for i in "${LUN_LIST[@]}"; do
    MNT="/mnt/lun${i}"
    SOCK="/tmp/nbd${i}.sock"
    DEV="/dev/nbd${NBD_INDEX}"

    if [ -n "$HTTP_DISK_URL" ]; then
        nbdkit -r -U "$SOCK" curl "$HTTP_DISK_URL" \
            protocols=http,https timeout=30 tcp-keepalive=true tcp-nodelay=true
        nbd-client -unix "$SOCK" "$DEV"
    else
        mkdir -p "$MNT"
        GOGC=25 rclone mount ":webdav:Xbox_LUN${i}" "$MNT" \
            --webdav-url "$SERVER_URL" \
            --read-only \
            --vfs-cache-mode off \
            --buffer-size 4M \
            --vfs-read-chunk-size 4M \
            --vfs-read-chunk-size-limit 16M \
            --dir-cache-time 24h \
            --attr-timeout 24h \
            --no-modtime \
            --no-checksum \
            --transfers 1 \
            --checkers 2 \
            --use-mmap \
            --daemon
        sleep 2
        nbdkit --filter=cow -U "$SOCK" floppy "$MNT" label="XBOX_USB${i}" \
            cow-on-read=false cow-on-cache=false
        nbd-client -unix "$SOCK" "$DEV"
        /usr/local/bin/x360-fat32.py "$DEV"
    fi

    if [ -z "$NBD_DEVICES" ]; then
        NBD_DEVICES="$DEV"
    else
        NBD_DEVICES="${NBD_DEVICES},${DEV}"
    fi

    NBD_INDEX=$((NBD_INDEX + 1))
done

# 6. Expose all virtual LUNs over USB OTG to Xbox 360
USB_SERIAL=$(tr -d '\0' < /sys/firmware/devicetree/base/serial-number)
if [ -z "$USB_SERIAL" ]; then
    echo "[X360-NetDrive] Cannot determine the Pi USB serial number." >&2
    exit 1
fi
modprobe g_mass_storage file="$NBD_DEVICES" stall=0 removable=1 ro=1 idVendor=0x0781 idProduct=0x5575 iManufacturer="SanDisk" iProduct="X360-NetDrive" iSerialNumber="$USB_SERIAL"
echo "[X360-NetDrive] Bridge active on $NBD_DEVICES"
