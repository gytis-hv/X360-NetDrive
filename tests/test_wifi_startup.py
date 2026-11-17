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
fi
"""
        )
        stub.chmod(0o755)
        for name in (
            "raspi-config", "rfkill", "nmcli", "ip", "curl", "modprobe",
            "rclone", "nbdkit", "nbd-client", "sleep",
        ):
            (self.root / name).symlink_to(stub)
        self.env = dict(
            os.environ,
            PATH=f"{self.root}:{os.environ['PATH']}",
            CONFIG_FILE=str(self.config),
            CALLS=str(self.calls),
            FAIL_WIFI="0",
            NBD_CLIENT_STATUS="0",
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
        self.assertIn("HTTP_DISK_URL must be an HTTP(S) URL", result.stderr)
        self.assertEqual(self.calls.read_text(), "")

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
        self.assertEqual(len(nbdkit_calls), 1)
        self.assertRegex(nbdkit_calls[0], rf"^nbdkit -r -U \S+ curl {disk_url} ")
        self.assertEqual(nbd_client_calls, ["nbd-client -unix /tmp/nbd1.sock /dev/nbd0"])
        self.assertFalse(any(line.startswith("rclone ") for line in calls))
        self.assertFalse(any("floppy" in line for line in nbdkit_calls))
        self.assertFalse(any("x360-fat32.py" in line for line in calls))
        self.assertFalse(any("g_mass_storage" in line for line in calls))


if __name__ == "__main__":
    unittest.main()
