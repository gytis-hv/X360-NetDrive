#!/bin/bash
set -euo pipefail

BOOT=/boot/firmware
TEMP="$BOOT/x360-debug.log.tmp"
RESTORE_RO=0

if ! mountpoint -q "$BOOT"; then
    echo "[X360-NetDrive] Boot partition is not mounted; cannot save diagnostics." >&2
    exit 1
fi

cleanup() {
    status=$?
    trap - EXIT
    if ! rm -f "$TEMP"; then
        echo "[X360-NetDrive] Cannot remove temporary diagnostic snapshot." >&2
        status=1
    fi
    if [ "$RESTORE_RO" -eq 1 ] && ! mount -o remount,ro "$BOOT"; then
        echo "[X360-NetDrive] Cannot restore read-only boot partition." >&2
        status=1
    fi
    exit "$status"
}
trap cleanup EXIT

if findmnt -no OPTIONS --target "$BOOT" | grep -Eq '(^|,)ro(,|$)'; then
    mount -o remount,rw "$BOOT"
    RESTORE_RO=1
fi

{
    date -Is
    uptime
    echo "--- Network addresses ---"
    ip -br address
    echo "--- Wi-Fi devices ---"
    if ! nmcli --wait 5 -f DEVICE,TYPE,STATE,CONNECTION device status; then
        echo "NetworkManager device status failed."
    fi
    echo "--- Service states ---"
    systemctl show x360-netdrive-wifi.service x360-netdrive.service ssh.service \
        -p Id -p ActiveState -p SubState -p Result
    echo "--- Recent service logs ---"
    journalctl -b --no-pager -n 100 -o short-iso \
        -u NetworkManager.service -u x360-netdrive-wifi.service \
        -u x360-netdrive.service -u ssh.service
} | tail -c 131072 > "$TEMP"
mv "$TEMP" "$BOOT/x360-debug.log"
sync -f "$BOOT/x360-debug.log"
echo "[X360-NetDrive] Saved $BOOT/x360-debug.log; current addresses:"
ip -br address
