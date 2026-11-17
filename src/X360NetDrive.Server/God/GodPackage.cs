using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.God;

/// <summary>
/// An unsigned SVOD content tree. It is not a Microsoft-signed or licensed
/// Games on Demand package and cannot be authenticated by a stock console.
/// The source and cache must remain unchanged and available while it is used.
/// </summary>
public sealed class GodPackage : IDisposable
{
    private const int BlockSize = 0x1000;
    private const int BlocksPerGroup = 0xcc;
    private const int GroupsPerPart = 0xcb;
    private const int GroupBytes = BlocksPerGroup * BlockSize;
    private const long PartBytes = (long)GroupsPerPart * GroupBytes;
    private const int HeaderSize = 0xb000;
    private readonly string cachePath;
    private readonly object disposalLock = new();
    private bool disposed;

    public VirtualDirectory ContentRoot { get; }
    public string PackageRelativePath { get; }

    /// <summary>No signing or license generation is performed.</summary>
    public bool IsSigned => false;

    private GodPackage(VirtualDirectory contentRoot, string packageRelativePath, string cachePath)
    {
        ContentRoot = contentRoot;
        PackageRelativePath = packageRelativePath;
        this.cachePath = cachePath;
    }

    /// <summary>Deletes this package's cache after all reads have stopped; the source remains caller-owned.</summary>
    public void Dispose()
    {
        lock (disposalLock)
        {
            if (disposed)
                return;
            File.Delete(cachePath);
            disposed = true;
        }
    }

    public static Task<GodPackage> CreateAsync(
        VirtualFile isoPartition, string titleName, uint titleId, uint mediaId,
        string cacheDirectory, CancellationToken cancellationToken) =>
        CreateAsync(isoPartition, titleName, titleId, mediaId, cacheDirectory,
            version: 0, cancellationToken: cancellationToken);

    /// <summary>
    /// Builds the hash index by scanning the complete partition; payload bytes
    /// remain in the supplied virtual file. The cache directory must be outside
    /// the repository and have space for approximately one 4 KiB hash table
    /// per 0xcc payload blocks, plus one per data part.
    /// </summary>
    public static async Task<GodPackage> CreateAsync(
        VirtualFile isoPartition, string titleName, uint titleId, uint mediaId,
        string cacheDirectory, uint version = 0, uint baseVersion = 0,
        byte discNumber = 1, byte discCount = 1, byte platform = 0, byte executableType = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isoPartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (titleName.Contains('\0') || Encoding.BigEndianUnicode.GetByteCount(titleName) > 126)
            throw new ArgumentException("Title must fit in a 128-byte UTF-16BE field.", nameof(titleName));
        if (discNumber == 0 || discCount == 0 || discNumber > discCount)
            throw new ArgumentOutOfRangeException(nameof(discNumber), "Invalid disc number or count.");
        if (isoPartition.Length < 0x10800 || isoPartition.Length % 0x800 != 0)
            throw new InvalidDataException("The game partition must contain whole 2048-byte sectors.");

        var descriptor = new byte[0x800];
        await isoPartition.ReadExactlyAsync(0x10000, descriptor, cancellationToken);
        if (!descriptor.AsSpan(0, 20).SequenceEqual("MICROSOFT*XBOX*MEDIA"u8) ||
            !descriptor.AsSpan(0x7ec, 20).SequenceEqual("MICROSOFT*XBOX*MEDIA"u8))
            throw new InvalidDataException("Missing XDVDFS volume descriptor at partition offset 0x10000.");
        long rootStart = (long)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(20)) * 0x800;
        long rootLength = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(24));
        if (rootLength == 0 || rootStart > isoPartition.Length || rootLength > isoPartition.Length - rootStart)
            throw new InvalidDataException("XDVDFS root directory is outside the partition.");

        var blocks = (isoPartition.Length + BlockSize - 1) / BlockSize;
        if (blocks > 0xffffff)
            throw new NotSupportedException("The SVOD block count exceeds its 24-bit field.");
        var partCount = checked((int)((isoPartition.Length + PartBytes - 1) / PartBytes));
        if (partCount > 10000)
            throw new NotSupportedException("More than 10000 SVOD data parts are not supported.");

        Directory.CreateDirectory(cacheDirectory);
        string cachePath = Path.Combine(Path.GetFullPath(cacheDirectory), $"svod-{Guid.NewGuid():N}.hashes");
        var partOffsets = new long[partCount];
        var partLengths = new long[partCount];
        byte[] firstMasterHash;
        bool cacheCreated = false;
        try
        {
            await using var cache = new FileStream(cachePath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read, GroupBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
            cacheCreated = true;
            byte[] data = new byte[GroupBytes];
            byte[] table = new byte[BlockSize];
            long sourceOffset = 0;
            long totalPartSize = 0;
            for (int part = 0; part < partCount; part++)
            {
                partOffsets[part] = cache.Position;
                var partSize = Math.Min(PartBytes, isoPartition.Length - sourceOffset);
                var groupCount = (int)((partSize + GroupBytes - 1) / GroupBytes);
                partLengths[part] = BlockSize + (long)groupCount * BlockSize + partSize;
                totalPartSize += partLengths[part];
                byte[] master = new byte[BlockSize];
                await cache.WriteAsync(master, cancellationToken);
                long remaining = partSize;
                for (int group = 0; group < groupCount; group++)
                {
                    int count = (int)Math.Min(GroupBytes, remaining);
                    await isoPartition.ReadExactlyAsync(sourceOffset, data.AsMemory(0, count), cancellationToken);
                    Array.Clear(table);
                    for (int blockOffset = 0, hashOffset = 0; blockOffset < count; blockOffset += BlockSize, hashOffset += 20)
                        SHA1.HashData(data.AsSpan(blockOffset, Math.Min(BlockSize, count - blockOffset)),
                            table.AsSpan(hashOffset, 20));
                    SHA1.HashData(table, master.AsSpan(group * 20, 20));
                    await cache.WriteAsync(table, cancellationToken);
                    sourceOffset += count;
                    remaining -= count;
                }
                long nextPosition = cache.Position;
                cache.Position = partOffsets[part];
                await cache.WriteAsync(master, cancellationToken);
                cache.Position = nextPosition;
            }

            // A full master has 203 hashes; the final slot links the next part.
            byte[] nextHash = [];
            for (int part = partCount - 1; part >= 0; part--)
            {
                cache.Position = partOffsets[part];
                await cache.ReadExactlyAsync(table, cancellationToken);
                if (nextHash.Length != 0)
                {
                    nextHash.CopyTo(table, GroupsPerPart * 20);
                    cache.Position = partOffsets[part];
                    await cache.WriteAsync(table, cancellationToken);
                }
                nextHash = SHA1.HashData(table);
            }
            firstMasterHash = nextHash;
            await cache.FlushAsync(cancellationToken);

            byte[] header = MakeHeader(titleName, titleId, mediaId, version, baseVersion,
                discNumber, discCount, platform, executableType, (int)blocks, partCount, totalPartSize, firstMasterHash);
            string title = titleId.ToString("X8");
            string package = mediaId.ToString("X8");
            string relativePath = $"0000000000000000/{title}/00007000/{package}";
            var root = new VirtualDirectory("Content", isoPartition.LastModified);
            var allUsers = new VirtualDirectory("0000000000000000", isoPartition.LastModified);
            var titleDir = new VirtualDirectory(title, isoPartition.LastModified);
            var contentType = new VirtualDirectory("00007000", isoPartition.LastModified);
            var partsDir = new VirtualDirectory(package + ".data", isoPartition.LastModified);
            root.Add(allUsers);
            allUsers.Add(titleDir);
            titleDir.Add(contentType);
            contentType.Add(new MemoryVirtualFile(package, header, isoPartition.LastModified));
            contentType.Add(partsDir);
            for (int part = 0; part < partCount; part++)
                partsDir.Add(new SvodPart($"Data{part:D4}", isoPartition, cachePath,
                    partOffsets[part], part * PartBytes, partLengths[part]));
            return new GodPackage(root, relativePath, cachePath);
        }
        catch
        {
            if (cacheCreated)
            {
                try { File.Delete(cachePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static byte[] MakeHeader(string title, uint titleId, uint mediaId, uint version,
        uint baseVersion, byte discNumber, byte discCount, byte platform, byte executableType,
        int blocks, int partCount,
        long totalPartSize, byte[] masterHash)
    {
        byte[] header = new byte[HeaderSize];
        "LIVE"u8.CopyTo(header);
        // The signature and license entries intentionally remain zero.
        Write32(0x340, 0xad0e);
        Write32(0x344, 0x7000);
        Write32(0x348, 2);
        Write32(0x354, mediaId);
        Write32(0x358, version);
        Write32(0x35c, baseVersion);
        Write32(0x360, titleId);
        header[0x364] = platform;
        header[0x365] = executableType;
        header[0x366] = discNumber;
        header[0x367] = discCount;
        header[0x379] = 0x24;
        header[0x37a] = 5;
        header[0x37b] = 5;
        header[0x37c] = 0x11;
        masterHash.CopyTo(header, 0x37d);
        header[0x392] = (byte)(blocks >> 16);
        header[0x393] = (byte)(blocks >> 8);
        header[0x394] = (byte)blocks;
        Write32(0x39d, (uint)partCount);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x3a1), (ulong)totalPartSize);
        Write32(0x3a9, 1);
        Encoding.BigEndianUnicode.GetBytes(title, header.AsSpan(0x411, 0x80));
        Encoding.BigEndianUnicode.GetBytes(title, header.AsSpan(0x1691, 0x80));
        SHA1.HashData(header.AsSpan(0x344, HeaderSize - 0x344), header.AsSpan(0x32c, 20));
        return header;

        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset), value);
    }

    private sealed class SvodPart(string name, VirtualFile source, string cachePath,
        long cacheOffset, long sourceStart, long length) : VirtualFile(name, source.LastModified)
    {
        public override long Length => length;

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            cancellationToken.ThrowIfCancellationRequested();
            if (offset >= Length || buffer.IsEmpty)
                return 0;
            int wanted = (int)Math.Min(buffer.Length, Length - offset);
            int done = 0;
            while (done < wanted)
            {
                long position = offset + done;
                long indexOffset;
                long payloadOffset;
                int count;
                if (position < BlockSize)
                {
                    indexOffset = cacheOffset + position;
                    payloadOffset = -1;
                    count = (int)Math.Min(wanted - done, BlockSize - position);
                }
                else
                {
                    long relative = position - BlockSize;
                    long group = relative / (BlockSize + GroupBytes);
                    int inGroup = (int)(relative % (BlockSize + GroupBytes));
                    if (inGroup < BlockSize)
                    {
                        indexOffset = cacheOffset + BlockSize + group * BlockSize + inGroup;
                        payloadOffset = -1;
                        count = Math.Min(wanted - done, BlockSize - inGroup);
                    }
                    else
                    {
                        indexOffset = -1;
                        payloadOffset = sourceStart + group * GroupBytes + inGroup - BlockSize;
                        count = Math.Min(wanted - done, BlockSize + GroupBytes - inGroup);
                    }
                }
                if (payloadOffset >= 0)
                    await source.ReadExactlyAsync(payloadOffset, buffer.Slice(done, count), cancellationToken);
                else
                {
                    using var handle = File.OpenHandle(cachePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    int read = 0;
                    while (read < count)
                    {
                        int n = await RandomAccess.ReadAsync(handle, buffer.Slice(done + read, count - read),
                            indexOffset + read, cancellationToken);
                        if (n == 0)
                            throw new EndOfStreamException("SVOD hash cache was truncated.");
                        read += n;
                    }
                }
                done += count;
            }
            return done;
        }
    }
}
