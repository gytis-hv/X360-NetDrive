#!/bin/bash
set -euo pipefail

BOOT_DIR="${1:-/boot/firmware}"
MODE="${2:-prepare}"
if [[ "$MODE" != prepare && "$MODE" != verify ]]; then
    echo "Expected public-image access mode: prepare or verify." >&2
    exit 1
fi
for marker in userconf.txt ssh ssh.txt; do
    if [ -e "$BOOT_DIR/$marker" ]; then
        echo "Public image contains unexpected boot credential or SSH marker: $marker" >&2
        exit 1
    fi
done

if ! getent passwd | awk -F: '$3 >= 1000 && $3 < 65534 { found = 1 } END { exit found }'; then
    echo "Public image contains an interactive user account." >&2
    exit 1
fi

if [ "$MODE" = prepare ]; then
    passwd -l root
fi
root_hash=$(getent shadow root | cut -d: -f2)
if [[ "$root_hash" != '!'* && "$root_hash" != '*'* ]]; then
    echo "Public image root account is not locked." >&2
    exit 1
fi
if [ "$MODE" = prepare ]; then
    systemctl mask userconfig.service ssh.service ssh.socket \
        getty@.service serial-getty@.service getty@tty1.service \
        serial-getty@ttyAMA0.service serial-getty@serial0.service
fi
for unit in userconfig.service ssh.service ssh.socket getty@.service serial-getty@.service \
    getty@tty1.service serial-getty@ttyAMA0.service serial-getty@serial0.service; do
    if state=$(systemctl is-enabled "$unit"); then
        :
    elif [ "$state" != masked ]; then
        echo "Could not verify public image service is masked: $unit" >&2
        exit 1
    fi
    if [ "$state" != masked ]; then
        echo "Public image service is not masked: $unit" >&2
        exit 1
    fi
done
