using System.Buffers.Binary;
using System.Text;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Fat;

/// <summary>A read-only, on-demand MBR disk containing a single FAT32 volume.</summary>
public sealed class Fat32Disk : VirtualFile
{
    private const int SectorSize = 512;
    private const int SectorsPerCluster = 64;
    private const int ClusterSize = SectorSize * SectorsPerCluster;
    private const uint PartitionStart = 2048;
    private const uint ReservedSectors = 32;
    private const uint MinimumClusters = 65536;
    private const uint ExtraFreeClusters = 2048;
    private const uint MaximumClusters = 0x0FFFFFEE;

    private readonly byte[] mbr;
    private readonly byte[] boot;
    private readonly byte[] fsInfo;
    private readonly Allocation[] allocations;
    private readonly Extent[] extents;
    private readonly long fatStart;
    private readonly long fatLength;
    private readonly long dataStart;
    private readonly uint clusterCount;
    public override long Length { get; }

    public Fat32Disk(VirtualDirectory root, string label = "X360NET", uint volumeId = 0x58333630)
        : base("disk.img", (root ?? throw new ArgumentNullException(nameof(root))).LastModified)
    {
        var volumeLabel = MakeVolumeLabel(label);
        var directories = new List<DirectoryLayout>();
        var seenDirectories = new HashSet<VirtualDirectory>(ReferenceEqualityComparer.Instance);
        var rootLayout = new DirectoryLayout(root, null);
        directories.Add(rootLayout);
        seenDirectories.Add(root);

        // Snapshot names, lengths and timestamps before assigning any disk addresses.
        for (var index = 0; index < directories.Count; index++)
        {
            var directory = directories[index];
            var children = directory.Source.Children.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Name, StringComparer.Ordinal).ToArray();
            var aliases = MakeAliases(children);
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                ValidateName(child.Name);
                if (child is VirtualDirectory subdirectory)
                {
                    if (!seenDirectories.Add(subdirectory))
                        throw new InvalidDataException("A directory cannot occur more than once in the FAT tree.");
                    var layout = new DirectoryLayout(subdirectory, directory);
                    directory.Children.Add(new Entry(child, aliases[i], 0, layout));
                    directories.Add(layout);
                }
                else if (child is VirtualFile file)
                {
                    var size = file.Length;
                    if (size < 0 || size > uint.MaxValue)
                        throw new InvalidDataException($"File exceeds the FAT32 size limit: {file.Name}");
                    directory.Children.Add(new Entry(file, aliases[i], (uint)size, null));
                }
                else
                    throw new InvalidDataException($"Unsupported virtual node: {child.Name}");
            }
        }

        ulong nextCluster = 2;
        var allocated = new List<Allocation>();
        foreach (var directory in directories)
        {
            long entryCount = directory.Parent is null ? 1 : 2;
            foreach (var child in directory.Children)
                entryCount = checked(entryCount + 1 + LongNameEntryCount(child));
            var bytes = checked((entryCount + 1) * 32); // terminating zero entry
            directory.ByteLength = checked((int)bytes);
            directory.Cluster = Allocate(bytes, ref nextCluster, allocated);
        }
        foreach (var directory in directories)
            foreach (var child in directory.Children)
                if (child.Node is VirtualFile && child.Size != 0)
                    child.Cluster = Allocate(child.Size, ref nextCluster, allocated);

        var usedClusters = nextCluster - 2;
        var totalClusters = Math.Max((ulong)MinimumClusters, checked(usedClusters + ExtraFreeClusters));
        if (totalClusters > MaximumClusters)
            throw new InvalidDataException("FAT32 cluster limit exceeded.");
        clusterCount = (uint)totalClusters;
        var fatSectors = (totalClusters + 2 + 127) / 128;
        var partitionSectors = checked((ulong)ReservedSectors + 2 * fatSectors + totalClusters * SectorsPerCluster);
        if (partitionSectors > uint.MaxValue || PartitionStart + partitionSectors > (ulong)uint.MaxValue + 1)
            throw new InvalidDataException("MBR sector address limit exceeded.");

        fatStart = (long)(PartitionStart + ReservedSectors) * SectorSize;
        fatLength = (long)fatSectors * SectorSize;
        dataStart = fatStart + 2 * fatLength;
        Length = checked((long)(PartitionStart + partitionSectors) * SectorSize);
        allocations = allocated.ToArray();

        var dataExtents = new List<Extent>();
        foreach (var directory in directories)
        {
            var content = BuildDirectory(directory, volumeLabel);
            dataExtents.Add(new Extent(ClusterOffset(directory.Cluster), content.Length, content, null));
            foreach (var child in directory.Children)
                if (child.Node is VirtualFile file && child.Size != 0)
                    dataExtents.Add(new Extent(ClusterOffset(child.Cluster), child.Size, null, file));
        }
        extents = dataExtents.OrderBy(e => e.Start).ToArray();

        mbr = MakeMbr((uint)partitionSectors);
        boot = MakeBoot((uint)partitionSectors, (uint)fatSectors, rootLayout.Cluster, volumeLabel, volumeId);
        fsInfo = MakeFsInfo(clusterCount - (uint)usedClusters, (uint)nextCluster);
    }

    public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        if (offset >= Length || buffer.IsEmpty)
            return 0;
        var count = (int)Math.Min(buffer.Length, Length - offset);
        buffer = buffer[..count];
        buffer.Span.Clear();

        CopyMetadata(offset, buffer, 0, mbr);
        var partitionOffset = (long)PartitionStart * SectorSize;
        CopyMetadata(offset, buffer, partitionOffset, boot);
        CopyMetadata(offset, buffer, partitionOffset + SectorSize, fsInfo);
        CopyMetadata(offset, buffer, partitionOffset + 6 * SectorSize, boot);
        CopyMetadata(offset, buffer, partitionOffset + 7 * SectorSize, fsInfo);
        RenderFat(offset, buffer, fatStart);
        RenderFat(offset, buffer, fatStart + fatLength);

        var end = offset + count;
        var low = 0;
        var high = extents.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (extents[mid].Start + extents[mid].Length <= offset)
                low = mid + 1;
            else
                high = mid;
        }
        for (var i = low; i < extents.Length && extents[i].Start < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extent = extents[i];
            var start = Math.Max(offset, extent.Start);
            var length = (int)(Math.Min(end, extent.Start + extent.Length) - start);
            var destination = buffer.Slice((int)(start - offset), length);
            var sourceOffset = start - extent.Start;
            if (extent.Content is not null)
                extent.Content.AsMemory((int)sourceOffset, length).CopyTo(destination);
            else
                await extent.File!.ReadExactlyAsync(sourceOffset, destination, cancellationToken).ConfigureAwait(false);
        }
        return count;
    }

    private long ClusterOffset(uint cluster) => dataStart + ((long)cluster - 2) * ClusterSize;

    private static uint Allocate(long bytes, ref ulong nextCluster, List<Allocation> allocations)
    {
        var count = ((ulong)bytes + ClusterSize - 1) / ClusterSize;
        if (count == 0 || nextCluster + count - 2 > MaximumClusters)
            throw new InvalidDataException("FAT32 cluster limit exceeded.");
        var start = (uint)nextCluster;
        allocations.Add(new Allocation(start, (uint)count));
        nextCluster += count;
        return start;
    }

    private static void CopyMetadata(long offset, Memory<byte> buffer, long start, byte[] source)
    {
        var from = Math.Max(offset, start);
        var to = Math.Min(offset + buffer.Length, start + source.Length);
        if (from < to)
            source.AsMemory((int)(from - start), (int)(to - from)).CopyTo(buffer[(int)(from - offset)..]);
    }

    private void RenderFat(long offset, Memory<byte> buffer, long start)
    {
        var from = Math.Max(offset, start);
        var to = Math.Min(offset + buffer.Length, start + fatLength);
        if (from >= to)
            return;

        var firstEntry = (uint)((from - start) / 4);
        var low = 0;
        var high = allocations.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if ((ulong)allocations[mid].Start + allocations[mid].Count <= firstEntry)
                low = mid + 1;
            else
                high = mid;
        }
        var allocationIndex = low;
        var span = buffer.Span;
        for (var position = from; position < to;)
        {
            var relative = position - start;
            var entry = (uint)(relative / 4);
            while (allocationIndex < allocations.Length &&
                   (ulong)allocations[allocationIndex].Start + allocations[allocationIndex].Count <= entry)
                allocationIndex++;
            uint value = entry switch
            {
                0 => 0x0FFFFFF8,
                1 => 0x0FFFFFFF,
                _ => 0
            };
            if (allocationIndex < allocations.Length)
            {
                var allocation = allocations[allocationIndex];
                if (entry >= allocation.Start && (ulong)entry < (ulong)allocation.Start + allocation.Count)
                    value = entry == allocation.Start + allocation.Count - 1 ? 0x0FFFFFFF : entry + 1;
            }
            var byteIndex = (int)(relative % 4);
            var n = (int)Math.Min(4 - byteIndex, to - position);
            for (var j = 0; j < n; j++)
                span[(int)(position - offset) + j] = (byte)(value >> (8 * (byteIndex + j)));
            position += n;
        }
    }

    private static byte[] MakeMbr(uint partitionSectors)
    {
        var result = new byte[SectorSize];
        var entry = result.AsSpan(446, 16);
        entry[1] = 32; entry[2] = 33; // LBA 2048, geometry 255 heads / 63 sectors
        entry[4] = 0x0C;
        var lastSector = (ulong)PartitionStart + partitionSectors - 1;
        var cylinder = lastSector / (255 * 63);
        if (cylinder > 1023)
        {
            entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;
        }
        else
        {
            entry[5] = (byte)((lastSector / 63) % 255);
            entry[6] = (byte)((lastSector % 63 + 1) | ((cylinder >> 2) & 0xC0));
            entry[7] = (byte)cylinder;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], PartitionStart);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], partitionSectors);
        result[510] = 0x55; result[511] = 0xAA;
        return result;
    }

    private static byte[] MakeBoot(uint sectors, uint fatSectors, uint rootCluster, byte[] label, uint volumeId)
    {
        var result = new byte[SectorSize];
        result[0] = 0xEB; result[1] = 0x58; result[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(result, 3);
        Write16(result, 11, SectorSize);
        result[13] = SectorsPerCluster;
        Write16(result, 14, (ushort)ReservedSectors);
        result[16] = 2;
        result[21] = 0xF8;
        Write16(result, 24, 63); Write16(result, 26, 255);
        Write32(result, 28, PartitionStart);
        Write32(result, 32, sectors);
        Write32(result, 36, fatSectors);
        Write32(result, 44, rootCluster);
        Write16(result, 48, 1); Write16(result, 50, 6);
        result[64] = 0x80; result[66] = 0x29;
        Write32(result, 67, volumeId);
        label.CopyTo(result, 71);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(result, 82);
        result[510] = 0x55; result[511] = 0xAA;
        return result;
    }

    private static byte[] MakeFsInfo(uint freeClusters, uint nextFree)
    {
        var result = new byte[SectorSize];
        Write32(result, 0, 0x41615252);
        Write32(result, 484, 0x61417272);
        Write32(result, 488, freeClusters);
        Write32(result, 492, nextFree);
        Write32(result, 508, 0xAA550000);
        return result;
    }

    private static byte[] BuildDirectory(DirectoryLayout directory, byte[] volumeLabel)
    {
        var result = new byte[directory.ByteLength];
        var position = 0;
        if (directory.Parent is null)
        {
            volumeLabel.CopyTo(result, position);
            result[position + 11] = 0x08;
            WriteTimestamp(result, position, directory.Source.LastModified);
            position += 32;
        }
        else
        {
            WriteDirectoryEntry(result, position, Encoding.ASCII.GetBytes(".          "), 0x10,
                directory.Cluster, 0, directory.Source.LastModified);
            position += 32;
            var parentCluster = directory.Parent.Parent is null ? 0 : directory.Parent.Cluster;
            WriteDirectoryEntry(result, position, Encoding.ASCII.GetBytes("..         "), 0x10,
                parentCluster, 0, directory.Parent.Source.LastModified);
            position += 32;
        }

        foreach (var child in directory.Children)
        {
            var longCount = LongNameEntryCount(child);
            var checksum = AliasChecksum(child.Alias);
            for (var sequence = longCount; sequence > 0; sequence--)
            {
                WriteLongEntry(result, position, child.Node.Name, sequence, longCount, checksum);
                position += 32;
            }
            WriteDirectoryEntry(result, position, child.Alias, child.Directory is null ? (byte)0x20 : (byte)0x10,
                child.Directory?.Cluster ?? child.Cluster, child.Size, child.Node.LastModified);
            position += 32;
        }
        return result;
    }

    private static void WriteDirectoryEntry(byte[] bytes, int position, byte[] alias, byte attributes,
        uint cluster, uint size, DateTimeOffset timestamp)
    {
        alias.CopyTo(bytes, position);
        bytes[position + 11] = attributes;
        WriteTimestamp(bytes, position, timestamp);
        Write16(bytes, position + 20, (ushort)(cluster >> 16));
        Write16(bytes, position + 26, (ushort)cluster);
        Write32(bytes, position + 28, size);
    }

    private static void WriteTimestamp(byte[] bytes, int position, DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime;
        if (date.Year < 1980) date = new DateTime(1980, 1, 1);
        if (date.Year > 2107) date = new DateTime(2107, 12, 31, 23, 59, 58);
        var fatDate = (ushort)(((date.Year - 1980) << 9) | (date.Month << 5) | date.Day);
        var fatTime = (ushort)((date.Hour << 11) | (date.Minute << 5) | (date.Second / 2));
        Write16(bytes, position + 14, fatTime);
        Write16(bytes, position + 16, fatDate);
        Write16(bytes, position + 18, fatDate);
        Write16(bytes, position + 22, fatTime);
        Write16(bytes, position + 24, fatDate);
    }

    private static void WriteLongEntry(byte[] bytes, int position, string name, int sequence, int count, byte checksum)
    {
        bytes[position] = (byte)(sequence | (sequence == count ? 0x40 : 0));
        bytes[position + 11] = 0x0F;
        bytes[position + 13] = checksum;
        ReadOnlySpan<int> offsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        for (var i = 0; i < 13; i++)
        {
            var index = (sequence - 1) * 13 + i;
            var value = index < name.Length ? name[index] : index == name.Length ? 0 : 0xFFFF;
            Write16(bytes, position + offsets[i], (ushort)value);
        }
    }

    private static int LongNameEntryCount(Entry entry) =>
        string.Equals(entry.Node.Name, AliasText(entry.Alias), StringComparison.Ordinal)
            ? 0 : (entry.Node.Name.Length + 12) / 13;

    private static byte AliasChecksum(byte[] alias)
    {
        byte checksum = 0;
        foreach (var b in alias)
            checksum = (byte)(((checksum & 1) << 7) + (checksum >> 1) + b);
        return checksum;
    }

    private static byte[][] MakeAliases(VirtualNode[] nodes)
    {
        var result = new byte[nodes.Length][];
        var used = new HashSet<string>(StringComparer.Ordinal);
        // Reserve genuine short names before generating aliases, irrespective of sort order.
        for (var i = 0; i < nodes.Length; i++)
        {
            ValidateName(nodes[i].Name);
            if (TryShortName(nodes[i].Name, out var alias) && used.Add(Encoding.ASCII.GetString(alias)))
                result[i] = alias;
        }
        for (var i = 0; i < nodes.Length; i++)
        {
            if (result[i] is not null) continue;
            var name = nodes[i].Name;
            var dot = name.LastIndexOf('.');
            var basePart = SanitizeAlias(dot > 0 ? name[..dot] : name);
            var extension = SanitizeAlias(dot > 0 ? name[(dot + 1)..] : "");
            if (basePart.Length == 0) basePart = "_";
            if (extension.Length > 3) extension = extension[..3];
            for (var n = 1; ; n++)
            {
                var suffix = "~" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (suffix.Length >= 8)
                    throw new InvalidDataException("Too many conflicting FAT short names.");
                var stem = basePart[..Math.Min(basePart.Length, 8 - suffix.Length)] + suffix;
                var candidate = Encoding.ASCII.GetBytes(stem.PadRight(8) + extension.PadRight(3));
                if (used.Add(Encoding.ASCII.GetString(candidate)))
                {
                    result[i] = candidate;
                    break;
                }
            }
        }
        return result;
    }

    private static bool TryShortName(string name, out byte[] alias)
    {
        alias = [];
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? "" : name[(dot + 1)..];
        if (stem.Length is < 1 or > 8 || extension.Length > 3 || stem.Contains('.') ||
            (dot >= 0 && extension.Length == 0))
            return false;
        var upper = (stem + extension).ToUpperInvariant();
        if (upper.Length != stem.Length + extension.Length || upper.Any(c => !IsAliasCharacter(c)))
            return false;
        alias = Encoding.ASCII.GetBytes(stem.ToUpperInvariant().PadRight(8) + extension.ToUpperInvariant().PadRight(3));
        return true;
    }

    private static string SanitizeAlias(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToUpperInvariant())
            if (IsAliasCharacter(c)) builder.Append(c);
            else if (c != ' ' && c != '.') builder.Append('_');
        return builder.ToString();
    }

    private static bool IsAliasCharacter(char c) =>
        c is >= 'A' and <= 'Z' or >= '0' and <= '9' or
            '$' or '%' or '\'' or '-' or '_' or '@' or '~' or '`' or '!' or '(' or ')' or '{' or '}' or '^' or '#' or '&';

    private static string AliasText(byte[] alias)
    {
        var stem = Encoding.ASCII.GetString(alias, 0, 8).TrimEnd();
        var extension = Encoding.ASCII.GetString(alias, 8, 3).TrimEnd();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." ||
            name[^1] is ' ' or '.' || name.Any(c => c < 32 || c is '\uFFFE' or '\uFFFF' || "\"*/:<>?\\|".Contains(c)))
            throw new InvalidDataException($"Invalid FAT file name: {name}");
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]))
            {
                if (++i >= name.Length || !char.IsLowSurrogate(name[i]))
                    throw new InvalidDataException("Invalid UTF-16 in FAT file name.");
            }
            else if (char.IsLowSurrogate(name[i]))
                throw new InvalidDataException("Invalid UTF-16 in FAT file name.");
        }
    }

    private static byte[] MakeVolumeLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var upper = label.ToUpperInvariant();
        if (upper.Length is < 1 or > 11 || upper[0] == ' ' || upper[^1] == ' ' ||
            upper.Any(c => c != ' ' && !IsAliasCharacter(c)))
            throw new ArgumentException("Volume label must contain 1–11 valid ASCII FAT label characters.", nameof(label));
        return Encoding.ASCII.GetBytes(upper.PadRight(11));
    }

    private static void Write16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Write32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed class DirectoryLayout(VirtualDirectory source, DirectoryLayout? parent)
    {
        public VirtualDirectory Source { get; } = source;
        public DirectoryLayout? Parent { get; } = parent;
        public List<Entry> Children { get; } = [];
        public uint Cluster { get; set; }
        public int ByteLength { get; set; }
    }

    private sealed class Entry(VirtualNode node, byte[] alias, uint size, DirectoryLayout? directory)
    {
        public VirtualNode Node { get; } = node;
        public byte[] Alias { get; } = alias;
        public uint Size { get; } = size;
        public DirectoryLayout? Directory { get; } = directory;
        public uint Cluster { get; set; }
    }

    private readonly record struct Allocation(uint Start, uint Count);
    private readonly record struct Extent(long Start, long Length, byte[]? Content, VirtualFile? File);
}
