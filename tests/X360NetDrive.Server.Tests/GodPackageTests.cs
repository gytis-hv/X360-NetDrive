using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using X360NetDrive.Server.God;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class GodPackageTests
{
    private const int BlockSize = 0x1000;
    private const int BlocksPerGroup = 0xCC;
    private const int GroupsPerPart = 0xCB;
    private const int GroupBytes = BlocksPerGroup * BlockSize;
    private const long PartBytes = (long)GroupsPerPart * GroupBytes;
    private const uint TitleId = 0x1234ABCD;
    private const uint MediaId = 0x55667788;
    private static readonly DateTimeOffset Modified =
        new(2021, 3, 4, 5, 6, 8, TimeSpan.Zero);
    private static readonly byte[] FirstPartTail = [0xFE, 0xED, 0xFA, 0xCE];
    private static readonly byte[] SecondPartHead = [0xDE, 0xAD, 0xBE, 0xEF];

    [Fact]
    public async Task CreateAsync_BuildsUnsignedHeaderHashIndexesAndData0001Mapping()
    {
        var source = CreatePartition(PartBytes + 0x2000, includeMarkers: true);
        using var temporary = new TemporaryDirectory();
        using var package = await GodPackage.CreateAsync(source, "Synthetic Title", TitleId, MediaId,
            temporary.Path, version: 0x01020304, baseVersion: 0xA0B0C0D0,
            discNumber: 2, discCount: 3, platform: 0x11, executableType: 0x22);

        Assert.False(package.IsSigned);
        Assert.Equal($"0000000000000000/{TitleId:X8}/00007000/{MediaId:X8}",
            package.PackageRelativePath);
        var contentType = Assert.IsType<VirtualDirectory>(
            package.ContentRoot.Find($"0000000000000000/{TitleId:X8}/00007000"));
        var headerFile = Assert.IsAssignableFrom<VirtualFile>(contentType.Find($"{MediaId:X8}"));
        var parts = Assert.IsType<VirtualDirectory>(contentType.Find($"{MediaId:X8}.data"));
        var data0 = Assert.IsAssignableFrom<VirtualFile>(parts.Find("Data0000"));
        var data1 = Assert.IsAssignableFrom<VirtualFile>(parts.Find("Data0001"));

        Assert.Equal(BlockSize + GroupsPerPart * BlockSize + PartBytes, data0.Length);
        Assert.Equal(2L * BlockSize + 0x2000, data1.Length);
        var header = await ReadAsync(headerFile, 0, checked((int)headerFile.Length));
        Assert.Equal("LIVE", Encoding.ASCII.GetString(header, 0, 4));
        Assert.All(header.AsSpan(4, 0x32C - 4).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(MediaId, ReadBigEndianUInt32(header, 0x354));
        Assert.Equal(0x01020304u, ReadBigEndianUInt32(header, 0x358));
        Assert.Equal(0xA0B0C0D0u, ReadBigEndianUInt32(header, 0x35C));
        Assert.Equal(TitleId, ReadBigEndianUInt32(header, 0x360));
        Assert.Equal(new byte[] { 0x11, 0x22, 2, 3 }, header[0x364..0x368]);
        Assert.Equal(2u, ReadBigEndianUInt32(header, 0x39D));
        Assert.Equal((ulong)(data0.Length + data1.Length),
            BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x3A1, 8)));
        Assert.Equal(Encoding.BigEndianUnicode.GetBytes("Synthetic Title"),
            header.AsSpan(0x411, Encoding.BigEndianUnicode.GetByteCount("Synthetic Title")).ToArray());
        Assert.Equal(SHA1.HashData(header.AsSpan(0x344)), header[0x32C..0x340]);

        var master0 = await ReadAsync(data0, 0, BlockSize);
        var master1 = await ReadAsync(data1, 0, BlockSize);
        var groupTable1 = await ReadAsync(data1, BlockSize, BlockSize);
        Assert.Equal(SHA1.HashData(master0), header[0x37D..0x391]);
        Assert.Equal(SHA1.HashData(master1), master0[(GroupsPerPart * 20)..(GroupsPerPart * 20 + 20)]);
        Assert.Equal(SHA1.HashData(groupTable1), master1[..20]);
        Assert.Equal(SHA1.HashData(MakeExpectedBlock(SecondPartHead)), groupTable1[..20]);

        var firstPartTailOffset = PayloadOffset(PartBytes - FirstPartTail.Length);
        Assert.Equal(FirstPartTail, await ReadAsync(data0, firstPartTailOffset, FirstPartTail.Length));
        Assert.Equal(SecondPartHead, await ReadAsync(data1, 2 * BlockSize, SecondPartHead.Length));
        Assert.Equal(SecondPartHead, await ReadAsync(source, PartBytes, SecondPartHead.Length));

        var blockCount = (int)(header[0x392] << 16 | header[0x393] << 8 | header[0x394]);
        Assert.Equal((int)((source.Length + BlockSize - 1) / BlockSize), blockCount);
        Assert.Single(Directory.GetFiles(temporary.Path, "*.hashes"));
    }

    [Fact]
    public async Task CreateAsync_DisposeDeletesOnlyItsOwnCacheAndKeepsSourceCallerOwned()
    {
        var source = CreatePartition(0x12000);
        using var temporary = new TemporaryDirectory();
        var callerFile = Path.Combine(temporary.Path, "caller-owned.txt");
        await File.WriteAllTextAsync(callerFile, "preserve");
        var first = await GodPackage.CreateAsync(source, "First", TitleId, MediaId, temporary.Path);
        var second = await GodPackage.CreateAsync(source, "Second", TitleId + 1, MediaId + 1, temporary.Path);
        try
        {
            Assert.Equal(2, Directory.GetFiles(temporary.Path, "*.hashes").Length);

            first.Dispose();

            Assert.Single(Directory.GetFiles(temporary.Path, "*.hashes"));
            Assert.Equal("MICR", Encoding.ASCII.GetString(await ReadAsync(source, 0x10000, 4)));
            Assert.Equal("preserve", await File.ReadAllTextAsync(callerFile));
            second.Dispose();
            Assert.Empty(Directory.GetFiles(temporary.Path, "*.hashes"));
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public async Task CreateAsync_PositionalCancellationOverloadUsesDefaultMetadata()
    {
        var source = CreatePartition(0x12000);
        using var temporary = new TemporaryDirectory();
        using var package = await GodPackage.CreateAsync(source, "Defaults", TitleId, MediaId,
            temporary.Path, CancellationToken.None);
        var header = Assert.IsAssignableFrom<VirtualFile>(
            package.ContentRoot.Find($"0000000000000000/{TitleId:X8}/00007000/{MediaId:X8}"));
        var bytes = await ReadAsync(header, 0x358, 16);

        Assert.Equal(new byte[4], bytes[..4]);
        Assert.Equal(new byte[4], bytes[4..8]);
        Assert.Equal(TitleId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(new byte[] { 0, 0, 1, 1 }, bytes[12..16]);
    }

    [Theory]
    [InlineData((byte)0, (byte)1)]
    [InlineData((byte)1, (byte)0)]
    [InlineData((byte)2, (byte)1)]
    public async Task CreateAsync_RejectsInvalidDiscMetadata(byte discNumber, byte discCount)
    {
        var source = CreatePartition(0x12000);
        using var temporary = new TemporaryDirectory();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GodPackage.CreateAsync(
            source, "Invalid Disc", TitleId, MediaId, temporary.Path,
            discNumber: discNumber, discCount: discCount));
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public async Task CreateAsync_RejectsPartitionWithoutXdvdfsDescriptor()
    {
        var source = new SparseVirtualFile("missing-descriptor.iso", 0x12000);
        using var temporary = new TemporaryDirectory();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GodPackage.CreateAsync(source, "Synthetic", TitleId, MediaId, temporary.Path));

        Assert.Contains("descriptor", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    private static SparseVirtualFile CreatePartition(long length, bool includeMarkers = false)
    {
        var source = new SparseVirtualFile("synthetic-partition.iso", length);
        var descriptor = new byte[0x800];
        Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA").CopyTo(descriptor, 0);
        Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA").CopyTo(descriptor, 0x7EC);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(24), 1);
        source.Write(0x10000, descriptor);
        if (includeMarkers)
        {
            source.Write(PartBytes - FirstPartTail.Length, FirstPartTail);
            source.Write(PartBytes, SecondPartHead);
        }
        return source;
    }

    private static long PayloadOffset(long sourceOffset)
    {
        var group = sourceOffset / GroupBytes;
        var withinGroup = sourceOffset % GroupBytes;
        return BlockSize + group * (BlockSize + GroupBytes) + BlockSize + withinGroup;
    }

    private static byte[] MakeExpectedBlock(byte[] prefix)
    {
        var block = new byte[BlockSize];
        prefix.CopyTo(block, 0);
        return block;
    }

    private static async Task<byte[]> ReadAsync(VirtualFile file, long offset, int count)
    {
        var result = new byte[count];
        var read = await file.ReadAsync(offset, result);
        Assert.Equal(count, read);
        return result;
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
}
