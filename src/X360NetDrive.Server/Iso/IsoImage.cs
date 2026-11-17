using System.Buffers.Binary;
using System.Text;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Iso;

public sealed class IsoImage
{
    public required SliceVirtualFile Partition { get; init; }
    public required VirtualDirectory Root { get; init; }
    public required uint TitleId { get; init; }
    public required uint MediaId { get; init; }
    public required uint Version { get; init; }
    public required uint BaseVersion { get; init; }
    public required byte Platform { get; init; }
    public required byte ExecutableType { get; init; }
    public required byte DiscNumber { get; init; }
    public required byte DiscCount { get; init; }
    public required long PartitionOffset { get; init; }

    public static async Task<IsoImage> OpenAsync(VirtualFile source, CancellationToken cancellationToken = default)
    {
        var descriptor = new byte[2048];
        long? found = null;
        foreach (var offset in new long[] { 0, 0xFD90000, 0x2080000 })
        {
            if (source.Length < offset + 0x10800)
                continue;
            await source.ReadExactlyAsync(offset + 0x10000, descriptor, cancellationToken);
            if (descriptor.AsSpan(0, 20).SequenceEqual("MICROSOFT*XBOX*MEDIA"u8))
            {
                found = offset;
                break;
            }
        }
        if (found is not long partitionOffset)
            throw new InvalidDataException("No XDVDFS descriptor was found at a supported Xbox ISO partition offset.");
        var partition = new SliceVirtualFile("game-partition", source, partitionOffset, source.Length - partitionOffset);
        var reader = new DirectoryReader(partition, cancellationToken);
        var root = await reader.ReadAsync("", BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(20)),
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(24)), 0);
        if (root.Find("default.xex") is not VirtualFile executable)
            throw new InvalidDataException("The ISO does not contain default.xex.");
        var prefix = new byte[24];
        await executable.ReadExactlyAsync(0, prefix, cancellationToken);
        if (!prefix.AsSpan(0, 4).SequenceEqual("XEX2"u8))
            throw new InvalidDataException("default.xex is not an Xbox 360 XEX2 executable.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(20));
        if (count > 4096 || 24L + count * 8L > executable.Length)
            throw new InvalidDataException("Invalid XEX2 optional-header table.");
        var headers = new byte[checked((int)count * 8)];
        await executable.ReadExactlyAsync(24, headers, cancellationToken);
        for (var index = 0; index < count; index++)
        {
            var at = checked((int)index * 8);
            if (BinaryPrimitives.ReadUInt32BigEndian(headers.AsSpan(at)) != 0x00040006)
                continue;
            var infoOffset = BinaryPrimitives.ReadUInt32BigEndian(headers.AsSpan(at + 4));
            if ((long)infoOffset + 24 > executable.Length)
                throw new InvalidDataException("XEX execution metadata lies outside the executable.");
            var info = new byte[24];
            await executable.ReadExactlyAsync(infoOffset, info, cancellationToken);
            return new IsoImage
            {
                Partition = partition, Root = root, PartitionOffset = partitionOffset,
                MediaId = BinaryPrimitives.ReadUInt32BigEndian(info),
                Version = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(4)),
                BaseVersion = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(8)),
                TitleId = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(12)),
                Platform = info[16], ExecutableType = info[17],
                DiscNumber = info[18], DiscCount = info[19]
            };
        }
        throw new InvalidDataException("The XEX does not contain execution metadata.");
    }

    private sealed class DirectoryReader(VirtualFile partition, CancellationToken cancellationToken)
    {
        private readonly HashSet<(uint Sector, uint Size)> visited = [];
        private int entries;

        public async Task<VirtualDirectory> ReadAsync(string name, uint sector, uint size, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 64 || size > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid, cyclic, or excessively large ISO directory.");
            var directory = new VirtualDirectory(name, partition.LastModified);
            if (size == 0)
                return directory;
            if (!visited.Add((sector, size)))
                throw new InvalidDataException("Cyclic ISO directory extent.");
            var offset = (long)sector * 2048;
            if (offset > partition.Length || size > partition.Length - offset)
                throw new InvalidDataException("ISO directory lies outside the game partition.");
            var table = new byte[checked((int)size)];
            await partition.ReadExactlyAsync(offset, table, cancellationToken);
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            stack.Push(0);
            while (stack.TryPop(out var at))
            {
                if (!seen.Add(at) || at < 0 || at > table.Length - 14 || ++entries > 1_000_000)
                    throw new InvalidDataException("Invalid XDVDFS directory tree.");
                var nameLength = table[at + 13];
                if (nameLength == 0 || nameLength > table.Length - at - 14)
                    throw new InvalidDataException("Invalid XDVDFS file name.");
                var childName = Encoding.Latin1.GetString(table, at + 14, nameLength);
                var childSector = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4));
                var childSize = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 8));
                VirtualNode child = (table[at + 12] & 0x10) != 0
                    ? await ReadAsync(childName, childSector, childSize, depth + 1)
                    : new SliceVirtualFile(childName, partition, (long)childSector * 2048, childSize);
                directory.Add(child);
                var left = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at));
                var right = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at + 2));
                if (left != 0)
                    stack.Push(left * 4);
                if (right != 0)
                    stack.Push(right * 4);
            }
            return directory;
        }
    }
}
