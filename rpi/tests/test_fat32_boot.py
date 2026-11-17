import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location(
    "x360_fat32", Path(__file__).resolve().parents[1] / "scripts" / "x360-fat32.py"
)
FAT32 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FAT32)


class Fat32BootTests(unittest.TestCase):
    def make_disk(self, clusters=65538, jump=b"\0\0\0", bad_backup=False):
        disk = tempfile.TemporaryFile()
        self.addCleanup(disk.close)
        fat_sectors = ((clusters + 2) * 4 + 511) // 512
        sectors = 32 + 2 * fat_sectors + clusters * 32
        disk.truncate((2048 + sectors) * 512)
        mbr = bytearray(512)
        mbr[450] = 0x0C
        struct.pack_into("<II", mbr, 454, 2048, sectors)
        mbr[510:512] = b"\x55\xaa"
        disk.write(mbr)
        boot = bytearray(512)
        boot[:3] = jump
        struct.pack_into("<H", boot, 11, 512)
        boot[13] = 32
        struct.pack_into("<H", boot, 14, 32)
        boot[16] = 2
        struct.pack_into("<I", boot, 32, sectors)
        struct.pack_into("<I", boot, 36, fat_sectors)
        struct.pack_into("<H", boot, 50, 6)
        boot[82:90] = b"FAT32   "
        boot[510:512] = b"\x55\xaa"
        for sector in (2048, 2054):
            disk.seek(sector * 512)
            disk.write(boot)
        if bad_backup:
            disk.seek(2054 * 512 + 510)
            disk.write(b"\0\0")
        disk.seek((2048 + 32 + 2 * fat_sectors) * 512)
        disk.write(b"unchanged WebDAV data")
        disk.flush()
        return disk, bytes(mbr), bytes(boot), (2048 + 32 + 2 * fat_sectors) * 512

    def test_repairs_both_boot_jumps_without_changing_partition_or_content(self):
        disk, mbr, boot, data_offset = self.make_disk()
        self.assertEqual(FAT32.prepare_export(disk), (65538, 2))
        disk.seek(0)
        self.assertEqual(disk.read(512), mbr)
        for sector in (2048, 2054):
            disk.seek(sector * 512)
            self.assertEqual(disk.read(512), b"\xeb\x58\x90" + boot[3:])
        disk.seek(data_offset)
        self.assertEqual(disk.read(21), b"unchanged WebDAV data")

    def test_enforces_exact_fat32_minimum_before_changing_headers(self):
        for clusters in (65524, 65525):
            with self.subTest(clusters=clusters):
                disk, _, boot, _ = self.make_disk(clusters=clusters)
                if clusters == 65524:
                    with self.assertRaisesRegex(ValueError, "at least 65525"):
                        FAT32.prepare_export(disk)
                    for sector in (2048, 2054):
                        disk.seek(sector * 512)
                        self.assertEqual(disk.read(512), boot)
                else:
                    self.assertEqual(FAT32.prepare_export(disk), (65525, 2))

    def test_preserves_an_existing_valid_boot_jump(self):
        disk, _, boot, _ = self.make_disk(jump=b"\xeb\x3c\x90")
        self.assertEqual(FAT32.prepare_export(disk), (65538, 0))
        for sector in (2048, 2054):
            disk.seek(sector * 512)
            self.assertEqual(disk.read(512), boot)

    def test_invalid_backup_is_rejected_before_primary_is_modified(self):
        disk, _, boot, _ = self.make_disk(bad_backup=True)
        with self.assertRaisesRegex(ValueError, "headers do not match"):
            FAT32.prepare_export(disk)
        disk.seek(2048 * 512)
        self.assertEqual(disk.read(512), boot)


if __name__ == "__main__":
    unittest.main()
