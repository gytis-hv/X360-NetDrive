using System.Buffers.Binary;
using System.Text;
using X360NetDrive.Server.Fat;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class Fat32DiskTests
{
    private const int SectorSize = 512;
    private const int PartitionStart = 2048;
    private const int ClusterSize = SectorSize * 64;
    private static readonly DateTimeOffset Modified =
        new(2022, 8, 9, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task Fat32Disk_EmitsConsistentMbrBpbBackupBootAndFsInfo()
    {
        var disk = new Fat32Disk(new VirtualDirectory("", Modified), "TESTVOL", 0x12345678);
        var mbr = await ReadAsync(disk, 0, SectorSize);
        var entry = 446;
        var partitionSectors = ReadUInt32(mbr, entry + 12);
        var bootOffset = (long)PartitionStart * SectorSize;
        var boot = await ReadAsync(disk, bootOffset, SectorSize);
        var fsInfo = await ReadAsync(disk, bootOffset + SectorSize, SectorSize);

        Assert.Equal(new byte[] { 0x55, 0xAA }, mbr[510..512]);
        Assert.Equal((byte)0x0C, mbr[entry + 4]);
        Assert.Equal((uint)PartitionStart, ReadUInt32(mbr, entry + 8));
        Assert.Equal(disk.Length, ((long)PartitionStart + partitionSectors) * SectorSize);
        Assert.Equal(new byte[] { 0xEB, 0x58, 0x90 }, boot[..3]);
        Assert.Equal((ushort)SectorSize, ReadUInt16(boot, 11));
        Assert.Equal((byte)64, boot[13]);
        Assert.Equal((ushort)32, ReadUInt16(boot, 14));
        Assert.Equal((byte)2, boot[16]);
        Assert.Equal((uint)PartitionStart, ReadUInt32(boot, 28));
        Assert.Equal(partitionSectors, ReadUInt32(boot, 32));
        Assert.Equal((ushort)6, ReadUInt16(boot, 50));
        Assert.Equal(0x12345678u, ReadUInt32(boot, 67));
        Assert.Equal("TESTVOL    ", Encoding.ASCII.GetString(boot, 71, 11));
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(boot, 82, 8));
        Assert.Equal(new byte[] { 0x55, 0xAA }, boot[510..512]);

        var backupBoot = await ReadAsync(disk, bootOffset + 6 * SectorSize, SectorSize);
        var backupFsInfo = await ReadAsync(disk, bootOffset + 7 * SectorSize, SectorSize);
        Assert.Equal(boot, backupBoot);
        Assert.Equal(fsInfo, backupFsInfo);
        Assert.Equal(0x41615252u, ReadUInt32(fsInfo, 0));
        Assert.Equal(0x61417272u, ReadUInt32(fsInfo, 484));
        Assert.Equal(0xAA550000u, ReadUInt32(fsInfo, 508));
        Assert.Equal(3u, ReadUInt32(fsInfo, 492));
    }

    [Fact]
    public async Task Fat32Disk_AllocatesAtLeastTheFat32MinimumClustersAndMirrorsFatCopies()
    {
        var disk = new Fat32Disk(new VirtualDirectory("", Modified));
        var boot = await ReadAsync(disk, (long)PartitionStart * SectorSize, SectorSize);
        var totalSectors = ReadUInt32(boot, 32);
        var fatSectors = ReadUInt32(boot, 36);
        var clusterCount = (totalSectors - 32 - 2 * fatSectors) / 64;
        var fatStart = (long)(PartitionStart + 32) * SectorSize;
        var fatLength = checked((int)(fatSectors * SectorSize));
        var firstFat = await ReadAsync(disk, fatStart, fatLength);
        var secondFat = await ReadAsync(disk, fatStart + fatLength, fatLength);
        var fsInfo = await ReadAsync(disk, (long)PartitionStart * SectorSize + SectorSize, SectorSize);

        Assert.True(clusterCount >= 65_525, $"The generated volume has {clusterCount} data clusters.");
        Assert.Equal(firstFat, secondFat);
        Assert.Equal(0x0FFFFFF8u, ReadUInt32(firstFat, 0) & 0x0FFFFFFF);
        Assert.Equal(0x0FFFFFFFu, ReadUInt32(firstFat, 4) & 0x0FFFFFFF);
        Assert.Equal(0x0FFFFFFFu, ReadUInt32(firstFat, 8) & 0x0FFFFFFF);
        Assert.Equal(clusterCount - 1, ReadUInt32(fsInfo, 488));
    }

    [Fact]
    public async Task Fat32Disk_MapsFilesAndLongNamesToAllocatedContent()
    {
        const string folderName = "Long Folder Name";
        const string fileName = "payload with space and ü.bin";
        var payload = Enumerable.Range(0, ClusterSize + 17).Select(i => (byte)(i * 31)).ToArray();
        var root = new VirtualDirectory("", Modified);
        var folder = new VirtualDirectory(folderName, Modified);
        folder.Add(new MemoryVirtualFile("LONG FILE A.TXT", new byte[] { 1 }, Modified));
        folder.Add(new MemoryVirtualFile("LONG FILE B.TXT", new byte[] { 2 }, Modified));
        folder.Add(new MemoryVirtualFile("LONGFI~1.TXT", new byte[] { 3 }, Modified));
        folder.Add(new MemoryVirtualFile("empty.bin", Array.Empty<byte>(), Modified));
        folder.Add(new MemoryVirtualFile(fileName, payload, Modified));
        root.Add(folder);
        var disk = new Fat32Disk(root);

        var layout = await ReadLayoutAsync(disk);
        var rootEntries = ParseDirectory(await ReadClusterAsync(disk, layout, layout.RootCluster));
        var folderEntry = Assert.Single(rootEntries, entry => entry.Name == folderName);
        Assert.Equal((byte)0x10, folderEntry.Attributes);
        Assert.True(folderEntry.LongNameChecksum.HasValue);

        var folderEntries = ParseDirectory(await ReadClusterAsync(disk, layout, folderEntry.Cluster));
        var dot = Assert.Single(folderEntries, entry => entry.Name == ".");
        var dotDot = Assert.Single(folderEntries, entry => entry.Name == "..");
        Assert.Equal(folderEntry.Cluster, dot.Cluster);
        Assert.Equal(0u, dotDot.Cluster);
        var aliasA = Assert.Single(folderEntries, entry => entry.Name == "LONG FILE A.TXT");
        var aliasB = Assert.Single(folderEntries, entry => entry.Name == "LONG FILE B.TXT");
        var reservedAlias = Assert.Single(folderEntries, entry => entry.Name == "LONGFI~1.TXT");
        Assert.Equal("LONGFI~2.TXT", DecodeAlias(aliasA.Alias));
        Assert.Equal("LONGFI~3.TXT", DecodeAlias(aliasB.Alias));
        Assert.Equal("LONGFI~1.TXT", DecodeAlias(reservedAlias.Alias));
        var emptyFile = Assert.Single(folderEntries, entry => entry.Name == "empty.bin");
        Assert.Equal(0u, emptyFile.Size);
        Assert.Equal(0u, emptyFile.Cluster);
        var fileEntry = Assert.Single(folderEntries, entry => entry.Name == fileName);
        Assert.Equal((byte)0x20, fileEntry.Attributes);
        Assert.Equal((uint)payload.Length, fileEntry.Size);
        Assert.True(fileEntry.LongNameChecksum.HasValue);
        Assert.Equal(AliasChecksum(fileEntry.Alias), fileEntry.LongNameChecksum.Value);

        var firstCluster = await ReadFatEntryAsync(disk, layout, fileEntry.Cluster);
        var secondCluster = firstCluster & 0x0FFFFFFF;
        Assert.Equal(fileEntry.Cluster + 1, secondCluster);
        Assert.Equal(0x0FFFFFFFu, await ReadFatEntryAsync(disk, layout, secondCluster) & 0x0FFFFFFF);
        var firstPayload = await ReadAsync(disk, ClusterOffset(layout, fileEntry.Cluster), ClusterSize);
        var secondPayload = await ReadAsync(disk, ClusterOffset(layout, secondCluster), 17);
        Assert.Equal(payload[..ClusterSize], firstPayload);
        Assert.Equal(payload[ClusterSize..], secondPayload);
    }

    [Fact]
    public async Task Fat32Disk_ReadsAcrossMetadataAndZeroPaddingBoundaries()
    {
        var payload = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var root = new VirtualDirectory("", Modified);
        root.Add(new MemoryVirtualFile("RANGE.BIN", payload, Modified));
        var disk = new Fat32Disk(root);
        var mbr = await ReadAsync(disk, 0, SectorSize);
        var partitionOffset = (long)PartitionStart * SectorSize;
        var boot = await ReadAsync(disk, partitionOffset, SectorSize);
        var fsInfo = await ReadAsync(disk, partitionOffset + SectorSize, SectorSize);

        var mbrBoundary = await ReadAsync(disk, 500, 20);
        Assert.Equal(mbr[500..512], mbrBoundary[..12]);
        Assert.Equal(new byte[8], mbrBoundary[12..]);

        var metadataBoundary = await ReadAsync(disk, partitionOffset + 500, 24);
        Assert.Equal(boot[500..512], metadataBoundary[..12]);
        Assert.Equal(fsInfo[..12], metadataBoundary[12..]);

        var layout = await ReadLayoutAsync(disk);
        var entry = Assert.Single(ParseDirectory(await ReadClusterAsync(disk, layout, layout.RootCluster)),
            item => item.Name == "RANGE.BIN");
        var fileTail = await ReadAsync(disk, ClusterOffset(layout, entry.Cluster) + payload.Length - 2, 8);
        Assert.Equal(payload[^2..], fileTail[..2]);
        Assert.Equal(new byte[6], fileTail[2..]);
        Assert.Equal(new byte[32], await ReadAsync(disk, ClusterOffset(layout, entry.Cluster + 1), 32));
    }

    [Fact]
    public async Task Fat32Disk_LayoutIsIndependentOfChildInsertionOrder()
    {
        var firstRoot = MakeOrderedRoot("alpha", "Zulu");
        var secondRoot = MakeOrderedRoot("Zulu", "alpha");
        var firstDisk = new Fat32Disk(firstRoot, "SAME", 0xABCDEF01);
        var secondDisk = new Fat32Disk(secondRoot, "SAME", 0xABCDEF01);
        var firstLayout = await ReadLayoutAsync(firstDisk);
        var secondLayout = await ReadLayoutAsync(secondDisk);

        Assert.Equal(firstDisk.Length, secondDisk.Length);
        Assert.Equal(await ReadAsync(firstDisk, 0, SectorSize), await ReadAsync(secondDisk, 0, SectorSize));
        Assert.Equal(await ReadAsync(firstDisk, firstLayout.PartitionOffset, SectorSize),
            await ReadAsync(secondDisk, secondLayout.PartitionOffset, SectorSize));
        Assert.Equal(await ReadAsync(firstDisk, firstLayout.FatStart, 4096),
            await ReadAsync(secondDisk, secondLayout.FatStart, 4096));
        var firstRootEntries = ParseDirectory(await ReadClusterAsync(firstDisk, firstLayout, firstLayout.RootCluster));
        var secondRootEntries = ParseDirectory(await ReadClusterAsync(secondDisk, secondLayout, secondLayout.RootCluster));
        Assert.Equal(firstRootEntries.Select(entry => (entry.Name, entry.Cluster, entry.Size)),
            secondRootEntries.Select(entry => (entry.Name, entry.Cluster, entry.Size)));
    }

    [Fact]
    public void Fat32Disk_RejectsFileLargerThanFat32Limit()
    {
        var root = new VirtualDirectory("", Modified);
        root.Add(new LengthOnlyVirtualFile("too-large.bin", (long)uint.MaxValue + 1));

        var error = Assert.Throws<InvalidDataException>(() => new Fat32Disk(root));

        Assert.Contains("FAT32 size limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fat32Disk_AcceptsMaximumUnsignedFileSize()
    {
        var root = new VirtualDirectory("", Modified);
        root.Add(new LengthOnlyVirtualFile("max-size.bin", uint.MaxValue));
        var disk = new Fat32Disk(root);
        var layout = await ReadLayoutAsync(disk);
        var entries = ParseDirectory(await ReadClusterAsync(disk, layout, layout.RootCluster));
        var file = Assert.Single(entries, entry => entry.Name == "max-size.bin");

        Assert.Equal(uint.MaxValue, file.Size);
        Assert.True(disk.Length > uint.MaxValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOO-LONG-LABEL")]
    [InlineData("BAD?LABEL")]
    public void Fat32Disk_RejectsInvalidVolumeLabels(string label)
    {
        var root = new VirtualDirectory("", Modified);

        Assert.Throws<ArgumentException>(() => new Fat32Disk(root, label));
    }

    [Fact]
    public void Fat32Disk_RejectsInvalidFatFileNames()
    {
        var root = new VirtualDirectory("", Modified);
        root.Add(new MemoryVirtualFile("trailing.", new byte[] { 1 }, Modified));

        Assert.Throws<InvalidDataException>(() => new Fat32Disk(root));
    }

    [Fact]
    public void Fat32Disk_RejectsDirectoryInstanceReferencedMoreThanOnce()
    {
        var root = new VirtualDirectory("", Modified);
        var firstParent = new VirtualDirectory("First", Modified);
        var secondParent = new VirtualDirectory("Second", Modified);
        var shared = new VirtualDirectory("Shared", Modified);
        firstParent.Add(shared);
        secondParent.Add(shared);
        root.Add(firstParent);
        root.Add(secondParent);

        Assert.Throws<InvalidDataException>(() => new Fat32Disk(root));
    }

    private static VirtualDirectory MakeOrderedRoot(string first, string second)
    {
        var root = new VirtualDirectory("", Modified);
        foreach (var name in new[] { first, second })
            root.Add(new MemoryVirtualFile(name + ".bin",
                Encoding.ASCII.GetBytes(name + "-content"), Modified));
        return root;
    }

    private static async Task<DiskLayout> ReadLayoutAsync(Fat32Disk disk)
    {
        var bootOffset = (long)PartitionStart * SectorSize;
        var boot = await ReadAsync(disk, bootOffset, SectorSize);
        var fatSectors = ReadUInt32(boot, 36);
        var rootCluster = ReadUInt32(boot, 44);
        var fatStart = (long)(PartitionStart + 32) * SectorSize;
        var dataStart = fatStart + 2L * fatSectors * SectorSize;
        return new DiskLayout(bootOffset, rootCluster, fatStart, dataStart, fatSectors);
    }

    private static async Task<byte[]> ReadClusterAsync(Fat32Disk disk, DiskLayout layout, uint cluster) =>
        await ReadAsync(disk, ClusterOffset(layout, cluster), ClusterSize);

    private static long ClusterOffset(DiskLayout layout, uint cluster) =>
        layout.DataStart + ((long)cluster - 2) * ClusterSize;

    private static async Task<uint> ReadFatEntryAsync(Fat32Disk disk, DiskLayout layout, uint cluster)
    {
        var value = await ReadAsync(disk, layout.FatStart + cluster * 4L, 4);
        return ReadUInt32(value, 0);
    }

    private static async Task<byte[]> ReadAsync(VirtualFile file, long offset, int count)
    {
        var result = new byte[count];
        var read = await file.ReadAsync(offset, result);
        Assert.Equal(count, read);
        return result;
    }

    private static List<FatEntry> ParseDirectory(byte[] bytes)
    {
        var result = new List<FatEntry>();
        SortedDictionary<int, string>? longNameParts = null;
        byte? longNameChecksum = null;
        for (var offset = 0; offset + 32 <= bytes.Length; offset += 32)
        {
            if (bytes[offset] == 0)
                break;
            var attributes = bytes[offset + 11];
            if (attributes == 0x0F)
            {
                longNameParts ??= new SortedDictionary<int, string>();
                longNameParts[bytes[offset] & 0x1F] = ReadLongNamePart(bytes, offset);
                longNameChecksum = bytes[offset + 13];
                continue;
            }
            if ((attributes & 0x08) != 0)
            {
                longNameParts = null;
                longNameChecksum = null;
                continue;
            }

            var alias = bytes.AsSpan(offset, 11).ToArray();
            var name = longNameParts is null ? DecodeAlias(alias) : DecodeLongName(longNameParts);
            var cluster = ((uint)ReadUInt16(bytes, offset + 20) << 16) | ReadUInt16(bytes, offset + 26);
            result.Add(new FatEntry(name, alias, attributes, cluster, ReadUInt32(bytes, offset + 28),
                longNameChecksum));
            longNameParts = null;
            longNameChecksum = null;
        }
        return result;
    }

    private static string ReadLongNamePart(byte[] bytes, int offset)
    {
        int[] positions = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        var characters = new char[positions.Length];
        for (var i = 0; i < positions.Length; i++)
            characters[i] = (char)ReadUInt16(bytes, offset + positions[i]);
        return new string(characters);
    }

    private static string DecodeLongName(SortedDictionary<int, string> parts)
    {
        var name = string.Concat(parts.Values);
        var terminator = name.IndexOf('\0');
        if (terminator >= 0)
            name = name[..terminator];
        return name.TrimEnd('\uFFFF');
    }

    private static string DecodeAlias(byte[] alias)
    {
        var stem = Encoding.ASCII.GetString(alias, 0, 8).TrimEnd();
        var extension = Encoding.ASCII.GetString(alias, 8, 3).TrimEnd();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }

    private static byte AliasChecksum(byte[] alias)
    {
        byte checksum = 0;
        foreach (var value in alias)
            checksum = (byte)(((checksum & 1) << 7) + (checksum >> 1) + value);
        return checksum;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private sealed record DiskLayout(long PartitionOffset, uint RootCluster, long FatStart, long DataStart,
        uint FatSectors);

    private sealed record FatEntry(string Name, byte[] Alias, byte Attributes, uint Cluster, uint Size,
        byte? LongNameChecksum);

    private sealed class LengthOnlyVirtualFile(string name, long length) : VirtualFile(name, Modified)
    {
        public override long Length { get; } = length;

        public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }
}
