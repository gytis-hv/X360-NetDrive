import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "prepare-public-image.sh"
UNITS = (
    "userconfig.service", "ssh.service", "ssh.socket",
    "getty@.service", "serial-getty@.service",
    "getty@tty1.service", "serial-getty@ttyAMA0.service", "serial-getty@serial0.service",
)


class PublicImageAccessTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.boot = root / "boot"
        self.boot.mkdir()
        self.calls = root / "calls"
        stub = root / "stub"
        stub.write_text(
            """#!/bin/bash
set -eu
command=${0##*/}
printf '%s\\n' "$command $*" >> "$CALLS"
case "$command $*" in
    'getent passwd')
        printf 'root:x:0:0:root:/root:/bin/bash\\n'
        if [ ! -f "$CALLS.deleted" ]; then
            printf '%s' "${EXTRA_PASSWD:-}"
        fi
        ;;
    'userdel --remove pi')
        if [ "${USERDEL_STATUS:-0}" -ne 0 ]; then
            exit "$USERDEL_STATUS"
        fi
        touch "$CALLS.deleted"
        ;;
    'getent shadow root') printf 'root:%s:19000:0:99999:7:::\\n' "${ROOT_HASH:-!locked}" ;;
    'passwd -l root') exit "${PASSWD_STATUS:-0}" ;;
    'systemctl mask '*) exit "${MASK_STATUS:-0}" ;;
    'systemctl is-enabled '*) echo "${UNIT_STATE:-masked}"; exit 1 ;;
    *) exit 2 ;;
esac
"""
        )
        stub.chmod(0o755)
        for name in ("getent", "passwd", "systemctl", "userdel"):
            (root / name).symlink_to(stub)
        self.env = dict(os.environ, PATH=f"{root}:{os.environ['PATH']}", CALLS=str(self.calls))

    def run_script(self, mode="prepare"):
        return subprocess.run(
            ["bash", str(SCRIPT), str(self.boot), mode], env=self.env,
            capture_output=True, text=True, timeout=10,
        )

    def test_public_image_locks_root_and_masks_all_interactive_entry_points(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertIn("passwd -l root", calls)
        self.assertIn("systemctl mask " + " ".join(UNITS), calls)
        for unit in UNITS:
            self.assertIn("systemctl is-enabled " + unit, calls)

    def test_public_image_rejects_existing_user_before_changing_access(self):
        self.env["EXTRA_PASSWD"] = "existing:x:1000:1000::/home/existing:/bin/bash\n"
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("interactive user account", result.stderr)
        self.assertNotIn("passwd -l root", self.calls.read_text())

    def test_public_image_removes_base_placeholder_before_locking_access(self):
        self.env["EXTRA_PASSWD"] = "pi:x:1000:1000::/home/pi:/bin/bash\n"
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertLess(calls.index("userdel --remove pi"), calls.index("passwd -l root"))
        self.assertEqual(calls.count("getent passwd"), 2)

    def test_public_image_rejects_unexpected_accounts_without_removing_them(self):
        for accounts in (
            "pi:x:1000:1000::/home/pi:/bin/bash\nother:x:1001:1001::/home/other:/bin/bash\n",
            "pi:x:1001:1001::/home/pi:/bin/bash\n",
            "pi:x:1000:1000::/home/other:/bin/bash\n",
        ):
            with self.subTest(accounts=accounts):
                self.env["EXTRA_PASSWD"] = accounts
                result = self.run_script()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("interactive user account", result.stderr)
                self.assertNotIn("userdel", self.calls.read_text())
                self.assertNotIn("passwd -l root", self.calls.read_text())

    def test_public_image_surfaces_placeholder_removal_failure(self):
        self.env["EXTRA_PASSWD"] = "pi:x:1000:1000::/home/pi:/bin/bash\n"
        self.env["USERDEL_STATUS"] = "42"
        result = self.run_script()
        self.assertEqual(result.returncode, 42)
        self.assertNotIn("passwd -l root", self.calls.read_text())

    def test_public_image_verification_rejects_placeholder_without_removing_it(self):
        self.env["EXTRA_PASSWD"] = "pi:x:1000:1000::/home/pi:/bin/bash\n"
        result = self.run_script("verify")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("interactive user account", result.stderr)
        self.assertNotIn("userdel", self.calls.read_text())

    def test_public_image_rejects_boot_account_and_ssh_markers(self):
        for marker in ("userconf.txt", "ssh", "ssh.txt"):
            with self.subTest(marker=marker):
                path = self.boot / marker
                path.touch()
                result = self.run_script()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(marker, result.stderr)
                self.assertFalse(self.calls.exists())
                path.unlink()

    def test_public_image_rejects_unlocked_root(self):
        self.env["ROOT_HASH"] = "not-locked"
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("root account is not locked", result.stderr)
        self.assertNotIn("systemctl mask", self.calls.read_text())

    def test_public_image_rejects_unmasked_service(self):
        self.env["UNIT_STATE"] = "enabled"
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("service is masked", result.stderr)
        self.assertIn("systemctl is-enabled userconfig.service", self.calls.read_text())

    def test_public_image_surfaces_mask_failure(self):
        self.env["MASK_STATUS"] = "42"
        result = self.run_script()
        self.assertEqual(result.returncode, 42)
        self.assertNotIn("systemctl is-enabled", self.calls.read_text())

    def test_public_image_final_verification_does_not_change_access(self):
        result = self.run_script("verify")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text()
        self.assertNotIn("passwd -l root", calls)
        self.assertNotIn("systemctl mask", calls)
        for unit in UNITS:
            self.assertIn("systemctl is-enabled " + unit, calls)
