import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "x360-netdrive.sh"


class WifiStartupTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.config = self.root / "config.txt"
        self.calls = self.root / "calls.txt"
        self.calls.touch()
        stub = self.root / "stub"
        stub.write_text(
            """#!/bin/bash
set -eu
command=${0##*/}
printf '%s\\n' "$command $*" >> "$CALLS"
if [ "$command" = nmcli ] && [ "${1:-}" = -g ]; then
    echo X360NetWiFi
elif [ "$command" = nmcli ] && [ "${1:-}" = --wait ] && [ "${FAIL_WIFI:-0}" = 1 ]; then
    echo 'Wi-Fi association failed' >&2
    exit 10
elif [ "$command" = nbd-client ]; then
    exit "${NBD_CLIENT_STATUS:-0}"
elif [ "$command" = nbd-size ]; then
    exit "${NBD_SIZE_STATUS:-0}"
elif [ "$command" = control ]; then
    case "$2" in
        http://*|https://*) printf '%s\\n' "$2" ;;
        *) echo "server URL must be HTTP(S)" >&2; exit 1 ;;
    esac
fi
"""
        )
        stub.chmod(0o755)
        for name in (
            "raspi-config", "rfkill", "nmcli", "ip", "curl", "modprobe",
            "rclone", "nbdkit", "nbd-client", "nbd-size", "sleep", "mkdir", "control",
        ):
            (self.root / name).symlink_to(stub)
        self.env = dict(
            os.environ,
            PATH=f"{self.root}:{os.environ['PATH']}",
            CONFIG_FILE=str(self.config),
            CALLS=str(self.calls),
            FAIL_WIFI="0",
            NBD_CLIENT_STATUS="0",
            NBD_SIZE_STATUS="0",
            NBD_SIZE_HELPER=str(self.root / "nbd-size"),
            CONTROL_BIN=str(self.root / "control"),
            STATE_DIR=str(self.root / "state"),
        )
        self.config.write_bytes(
            b'WIFI_SSID="Test Network"\r\nWIFI_PASSWORD="test-only-secret"\r\n'
            b'WIFI_COUNTRY="US"\r\nSERVER_URL="http://unreachable.invalid"\r\n'
        )

    def run_wifi(self):
        return subprocess.run(
            ["bash", str(SCRIPT), "--wifi-only"],
            env=self.env,
            capture_output=True,
            text=True,
            timeout=10,
        )

    def run_bridge(self):
        return subprocess.run(
            ["bash", str(SCRIPT)],
            env=self.env,
            capture_output=True,
            text=True,
            timeout=10,
        )

    def run_action(self, action):
        return subprocess.run(
            ["bash", str(SCRIPT), action], env=self.env,
            capture_output=True, text=True, timeout=10,
        )

    def test_source_only_connects_disk_without_attaching_usb_and_records_device(self):
        self.config.write_text('HTTP_DISK_URL="http://disk.test/disk"\n')
        result = self.run_action("--source-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertIn("nbd-client -unix /tmp/nbd1.sock /dev/nbd0", calls)
        self.assertFalse(any("g_mass_storage" in line for line in calls))
        self.assertEqual((self.root / "state" / "devices").read_text(), "/dev/nbd0\n")

    def test_disconnect_releases_recorded_devices_and_clears_state(self):
        state = self.root / "state"
        state.mkdir()
        (state / "devices").write_text("/dev/nbd0\n/dev/nbd1\n")
        (state / "usb-files").write_text("/dev/nbd0\n/dev/nbd1\n")
        result = self.run_action("--disconnect")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertIn("nbd-client -d /dev/nbd0", calls)
        self.assertIn("nbd-client -d /dev/nbd1", calls)
        self.assertFalse((state / "devices").exists())
        self.assertFalse((state / "usb-files").exists())

    def test_managed_server_uses_one_read_only_lun_without_empty_media(self):
        self.config.write_text('HTTP_DISK_URL="ftp://obsolete.test/disk"\n')
        state = self.root / "state"
        state.mkdir()
        (state / "source.conf").write_text(
            "MANAGED_SLOTS=1\nSLOT_TYPES=()\nSLOT_URLS=()\n"
            "SLOT_TYPES[1]=disk\nSLOT_URLS[1]='http://first.test/disk'\n"
        )
        serial = self.root / "serial"
        serial.write_bytes(b"test-serial\0")
        self.env["USB_SERIAL_FILE"] = str(serial)

        result = self.run_action("--source-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertIn("nbd-client -unix /tmp/nbd1.sock /dev/nbd0", calls)
        self.assertEqual((state / "devices").read_text(), "/dev/nbd0\n")
        # A stale sparse mapping must never influence the fixed drive.
        (state / "usb-files").write_text("\n/dev/nbd7\n")
        self.assertFalse(any("g_mass_storage" in line for line in calls))
        result = self.run_action("--usb-attach")
        self.assertEqual(result.returncode, 0, result.stderr)
        gadget = next(
            line for line in self.calls.read_text().splitlines()
            if line.startswith("modprobe g_mass_storage ")
        )
        self.assertIn("file=/dev/nbd0 ", gadget)
        self.assertNotIn(",", gadget)
        self.assertIn("ro=1 ", gadget)
        self.assertIn("removable=1 ", gadget)
        self.assertIn("iSerialNumber=test-serial", gadget)
        self.assertFalse(any("unreachable.invalid" in line for line in calls))

    def test_managed_webdav_is_rejected(self):
        state = self.root / "state"
        state.mkdir()
        (state / "source.conf").write_text(
            "MANAGED_SLOTS=1\nSLOT_TYPES=()\nSLOT_URLS=()\n"
            "SLOT_TYPES[1]=webdav\nSLOT_URLS[1]='http://dav.test/library/'\n"
        )
        result = self.run_action("--source-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("WebDAV sources are no longer supported", result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertFalse(any(line.startswith(("modprobe ", "rclone ", "nbdkit ", "nbd-client ")) for line in calls))
        self.assertFalse((state / "devices").exists())

    def test_card_server_url_is_discovered_before_connecting(self):
        self.config.write_text('SERVER_URL="http://server.test/"\n')
        result = self.run_action("--source-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertIn("control --discover-server http://server.test/", calls)
        self.assertLess(calls.index("control --discover-server http://server.test/"),
                        next(i for i, line in enumerate(calls) if line.startswith("nbdkit ")))

    def test_empty_managed_assignments_do_not_connect_or_attach_drives(self):
        state = self.root / "state"
        state.mkdir()
        (state / "source.conf").write_text("MANAGED_SLOTS=1\nSLOT_TYPES=()\nSLOT_URLS=()\n")
        result = self.run_action("--source-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((state / "devices").read_text(), "")
        self.assertFalse((state / "usb-files").exists())
        self.assertFalse(any(
            line.startswith(("curl ", "nbdkit ", "nbd-client ", "rclone ")) or
            "g_mass_storage" in line
            for line in self.calls.read_text().splitlines()
        ))

    def test_unsupported_runtime_slots_fail_before_connecting_devices(self):
        state = self.root / "state"
        state.mkdir()
        for mapping in (
            "SLOT_TYPES[7]=disk\nSLOT_URLS[7]='http://server.test/disk'\n",
            "SLOT_TYPES[1]=disk\nSLOT_URLS[1]='http://server.test/disk'\n"
            "SLOT_TYPES[2]=disk\nSLOT_URLS[2]='http://other.test/disk'\n",
        ):
            (state / "source.conf").write_text(
                "MANAGED_SLOTS=1\nSLOT_TYPES=()\nSLOT_URLS=()\n" + mapping
            )
            result = self.run_action("--source-only")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Invalid fixed-drive mapping", result.stderr)
            self.assertFalse((state / "devices").exists())
            self.assertEqual(self.calls.read_text(), "")

    def test_usb_attach_rejects_multiple_sources_instead_of_exposing_extra_luns(self):
        state = self.root / "state"
        state.mkdir()
        (state / "devices").write_text("/dev/nbd0\n/dev/nbd1\n")
        result = self.run_action("--usb-attach")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Exactly one source", result.stderr)
        self.assertNotIn("g_mass_storage", self.calls.read_text())

    def test_disconnect_surfaces_device_check_failure_and_keeps_state(self):
        state = self.root / "state"
        state.mkdir()
        (state / "devices").write_text("/dev/nbd0\n")
        self.env["NBD_CLIENT_STATUS"] = "42"
        result = self.run_action("--disconnect")
        self.assertEqual(result.returncode, 42, result.stderr)
        self.assertIn("Cannot check NBD connection", result.stderr)
        self.assertTrue((state / "devices").exists())

    def test_usb_attach_without_source_fails_before_loading_gadget(self):
        result = self.run_action("--usb-attach")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Connect a source", result.stderr)
        self.assertNotIn("g_mass_storage", self.calls.read_text())

    def test_wifi_connects_without_waiting_for_webdav_or_loading_usb(self):
        result = self.run_wifi()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertIn(
            "nmcli --wait 30 device wifi connect Test Network password test-only-secret name X360NetWiFi",
            calls,
        )
        self.assertIn("ip -br address", calls)
        self.assertFalse(any(line.startswith(("curl ", "modprobe ")) for line in calls))
        self.assertNotIn("test-only-secret", result.stdout + result.stderr)

    def test_wifi_association_failure_is_reported_without_success_message(self):
        self.env["FAIL_WIFI"] = "1"
        result = self.run_wifi()
        self.assertEqual(result.returncode, 10)
        self.assertIn("Wi-Fi association failed", result.stderr)
        self.assertNotIn("Wi-Fi connected.", result.stdout)
        self.assertNotIn("ip -br address", self.calls.read_text())

    def test_missing_wifi_settings_fail_before_network_changes(self):
        self.config.write_text('WIFI_SSID="Your_WiFi_Name"\n')
        result = self.run_wifi()
        self.assertEqual(result.returncode, 1)
        self.assertIn("Configure WIFI_SSID", result.stderr)
        self.assertEqual(self.calls.read_text(), "")

    def test_invalid_http_disk_url_fails_before_network_or_device_effects(self):
        self.config.write_text(
            'HTTP_DISK_URL="ftp://disk.test/exports/snapshot/disk"\n'
        )

        result = self.run_bridge()

        self.assertEqual(result.returncode, 1)
        self.assertIn("server URL must be HTTP(S)", result.stderr)
        self.assertFalse(any(line.startswith(("curl ", "nbdkit ", "nbd-client ")) for line in self.calls.read_text().splitlines()))

    def test_http_disk_url_uses_read_only_curl_nbd_without_rclone_or_fat_repair(self):
        disk_url = "https://disk.test/exports/snapshot/disk"
        self.config.write_text(f'HTTP_DISK_URL="{disk_url}"\n')
        self.env["NBD_CLIENT_STATUS"] = "42"

        result = self.run_bridge()

        calls = self.calls.read_text().splitlines()
        nbdkit_calls = [line for line in calls if line.startswith("nbdkit ")]
        nbd_client_calls = [line for line in calls if line.startswith("nbd-client ")]
        self.assertEqual(result.returncode, 42, result.stderr)
        self.assertIn(
            f"curl -s --head --fail --connect-timeout 3 --max-time 10 {disk_url}",
            calls,
        )
        self.assertEqual(len(nbdkit_calls), 2)
        self.assertRegex(nbdkit_calls[0], rf"^nbdkit -r -U \S+-raw\.sock curl {disk_url} ")
        self.assertEqual(nbd_client_calls, ["nbd-client -unix /tmp/nbd1.sock /dev/nbd0"])
        self.assertFalse(any(line.startswith("rclone ") for line in calls))
        self.assertFalse(any("floppy" in line for line in nbdkit_calls))
        self.assertFalse(any("x360-fat32.py" in line for line in calls))
        self.assertFalse(any("g_mass_storage" in line for line in calls))

    def test_disk_source_checks_size_before_connecting_padded_export(self):
        self.config.write_text('HTTP_DISK_URL="http://disk.test/disk"\n')
        result = self.run_action("--source-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        raw = str(self.root / "state" / "nbd1-raw.sock")
        size = f"nbd-size {raw} 2199023255040"
        padded = (
            f"nbdkit --filter=truncate -U /tmp/nbd1.sock nbd socket={raw} "
            "truncate=2199023255040"
        )
        self.assertLess(calls.index(size), calls.index(padded))
        self.assertLess(calls.index(padded), calls.index("nbd-client -unix /tmp/nbd1.sock /dev/nbd0"))

    def test_source_size_check_failure_prevents_padding_and_usb_connection(self):
        self.config.write_text('HTTP_DISK_URL="http://disk.test/disk"\n')
        self.env["NBD_SIZE_STATUS"] = "42"
        result = self.run_action("--source-only")
        self.assertEqual(result.returncode, 42)
        calls = self.calls.read_text().splitlines()
        self.assertFalse(any("filter=truncate" in line or line.startswith("nbd-client ") for line in calls))


if __name__ == "__main__":
    unittest.main()
