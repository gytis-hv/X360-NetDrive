#!/usr/bin/python3
import argparse
import os
import struct


def prepare_export(disk):
    disk.seek(0)
    mbr = disk.read(512)
    if len(mbr) != 512 or mbr[510:512] != b"\x55\xaa" or mbr[450] not in (0x0B, 0x0C):
        raise ValueError("Expected an MBR with a FAT32 first partition.")
    start, size = struct.unpack_from("<II", mbr, 454)
    if start == 0 or size == 0 or disk.seek(0, os.SEEK_END) < (start + size) * 512:
        raise ValueError("FAT32 partition lies outside the export.")
    disk.seek(start * 512)
    boot = disk.read(512)
    if boot[82:90] != b"FAT32   " or boot[510:512] != b"\x55\xaa":
        raise ValueError("Expected a FAT32 boot sector.")
    sector_size = struct.unpack_from("<H", boot, 11)[0]
    cluster_sectors = boot[13]
    reserved = struct.unpack_from("<H", boot, 14)[0]
    fats = boot[16]
    sectors = struct.unpack_from("<I", boot, 32)[0]
    fat_sectors = struct.unpack_from("<I", boot, 36)[0]
    if sector_size != 512 or cluster_sectors == 0 or cluster_sectors & (cluster_sectors - 1):
        raise ValueError("Unsupported FAT32 sector or cluster size.")
    if sectors > size or reserved == 0 or fats == 0 or fat_sectors == 0:
        raise ValueError("Invalid FAT32 layout.")
    clusters = (sectors - reserved - fats * fat_sectors) // cluster_sectors
    if clusters < 65525:
        raise ValueError(
            f"FAT32 needs at least 65525 data clusters; this LUN has {clusters}. "
            "Add content or a 1 GiB sparse padding file to the WebDAV LUN folder."
        )
    if clusters + 2 > fat_sectors * 512 // 4:
        raise ValueError("FAT is too small for the declared data area.")

    backup = struct.unpack_from("<H", boot, 50)[0]
    positions = [start]
    if backup not in (0, 0xFFFF):
        if backup >= reserved:
            raise ValueError("Backup boot sector lies outside the reserved area.")
        positions.append(start + backup)
    changes = []
    for sector in positions:
        disk.seek(sector * 512)
        header = disk.read(512)
        if header[3:90] != boot[3:90] or header[510:512] != b"\x55\xaa":
            raise ValueError("Primary and backup FAT32 headers do not match.")
        if header[:3] == b"\0\0\0":
            changes.append((sector, b"\xeb\x58\x90" + header[3:]))
        elif not (header[0] == 0xE9 or (header[0] == 0xEB and header[2] == 0x90)):
            raise ValueError("Unrecognized FAT32 boot jump instruction.")
    for sector, header in changes:
        disk.seek(sector * 512)
        if disk.write(header) != 512:
            raise OSError("Incomplete FAT32 header write.")
    disk.flush()
    return clusters, len(changes)


def main():
    parser = argparse.ArgumentParser(description="Validate and prepare a COW-backed FAT32 export for Xbox.")
    parser.add_argument("device")
    args = parser.parse_args()
    try:
        with open(args.device, "r+b", buffering=0) as disk:
            clusters, corrected = prepare_export(disk)
            os.fsync(disk.fileno())
    except (OSError, ValueError) as error:
        parser.exit(1, f"Cannot prepare FAT32 export: {error}\n")
    print(f"FAT32 ready: {clusters} data clusters; corrected {corrected} boot sector(s).")


if __name__ == "__main__":
    main()