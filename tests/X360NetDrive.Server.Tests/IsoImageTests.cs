using System.Buffers.Binary;
using System.Text;
using X360NetDrive.Server.Iso;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class IsoImageTests
{
    private const int PartitionLength = 0x12000;
    private const uint RootSector = 1;
    private const uint DefaultExecutableSector = 3;
    private const uint NestedDirectorySector = 2;
    private const string DescriptorMagic = "MICROSOFT*XBOX*MEDIA";

    [Theory]
    [InlineData(0L)]
    [InlineData(0xFD90000L)]
    [InlineData(0x2080000L)]
    public async Task OpenAsync_FindsXdvdfsAtSupportedPartitionOffset(long partitionOffset)
    {
        var source = CreateImage(partitionOffset);

        var image = await IsoImage.OpenAsync(source);

        Assert.Equal(partitionOffset, image.PartitionOffset);
        Assert.Equal(0xA1B2C3D4u, image.TitleId);
        Assert.Equal(PartitionLength, image.Partition.Length);
    }

    [Fact]
    public async Task OpenAsync_RejectsUnrecognizedXdvdfsDescriptor()
    {
        var source = new SparseVirtualFile("not-an-xbox-iso.bin", PartitionLength);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("No XDVDFS descriptor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_MapsRootAndNestedFilesAndReadsBigEndianExecutionMetadata()
    {
        var source = CreateImage(0, Shape.NestedFile);

        var image = await IsoImage.OpenAsync(source);

        Assert.Equal(0x11223344u, image.MediaId);
        Assert.Equal(0x55667788u, image.Version);
        Assert.Equal(0x99AABBCCu, image.BaseVersion);
        Assert.Equal(0xA1B2C3D4u, image.TitleId);
        Assert.Equal((byte)0x57, image.Platform);
        Assert.Equal((byte)0xA3, image.ExecutableType);
        Assert.Equal((byte)2, image.DiscNumber);
        Assert.Equal((byte)3, image.DiscCount);
        Assert.IsType<SliceVirtualFile>(image.Root.Find("default.xex"));
        var nestedFile = Assert.IsType<SliceVirtualFile>(image.Root.Find("DATA/inside.bin"));
        var contents = new byte[4];
        await nestedFile.ReadExactlyAsync(0, contents);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, contents);
    }

    [Fact]
    public async Task OpenAsync_AllowsDistinctEmptyDirectoriesToShareZeroExtent()
    {
        var source = CreateImage(0, Shape.EmptyDirectories);

        var image = await IsoImage.OpenAsync(source);

        var first = Assert.IsType<VirtualDirectory>(image.Root.Find("EMPTYA"));
        var second = Assert.IsType<VirtualDirectory>(image.Root.Find("EMPTYB"));
        Assert.NotSame(first, second);
        Assert.Empty(first.Children);
        Assert.Empty(second.Children);
    }

    [Fact]
    public async Task OpenAsync_RejectsMalformedDirectoryTree()
    {
        var source = CreateImage(0, Shape.MalformedTree);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("directory tree", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_RejectsDirectoryExtentOutsidePartition()
    {
        var source = CreateImage(0, Shape.OutOfRangeDirectory);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_RejectsRepeatedDirectoryExtent()
    {
        var source = CreateImage(0, Shape.CyclicDirectoryExtent);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("Cyclic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_RejectsMalformedDirectoryName()
    {
        var source = CreateImage(0, Shape.EmptyName);

        await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));
    }

    [Fact]
    public async Task OpenAsync_RejectsMissingDefaultExecutable()
    {
        var source = CreateImage(0, Shape.MissingDefaultExecutable);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("default.xex", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_RejectsInvalidXexMagic()
    {
        var source = CreateImage(0, Shape.InvalidXexMagic);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("XEX2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_RejectsInvalidXexOptionalHeaderTable()
    {
        var source = CreateImage(0, Shape.InvalidOptionalHeaderCount);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("optional-header table", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_RejectsExecutionInfoOutsideExecutable()
    {
        var source = CreateImage(0, Shape.ExecutionInfoOutsideExecutable);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_RejectsXexWithoutExecutionMetadata()
    {
        var source = CreateImage(0, Shape.NoExecutionMetadata);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => IsoImage.OpenAsync(source));

        Assert.Contains("execution metadata", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SparseVirtualFile CreateImage(long partitionOffset, Shape shape = Shape.Simple)
    {
        var source = new SparseVirtualFile("synthetic.iso", partitionOffset + PartitionLength);
        var descriptor = new byte[2048];
        Encoding.ASCII.GetBytes(DescriptorMagic).CopyTo(descriptor, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(20), RootSector);
        var rootLength = shape is Shape.NestedFile or Shape.EmptyDirectories or
            Shape.OutOfRangeDirectory or Shape.CyclicDirectoryExtent ? 192u : 64u;
        if (shape is Shape.MalformedTree or Shape.EmptyName or Shape.MissingDefaultExecutable)
            rootLength = 64;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(24), rootLength);
        source.Write(partitionOffset + 0x10000, descriptor);

        var root = new byte[rootLength];
        switch (shape)
        {
            case Shape.Simple:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false);
                break;
            case Shape.NestedFile:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false, right: 16);
                WriteEntry(root, 64, "DATA", NestedDirectorySector, 32, isDirectory: true);
                var nested = new byte[32];
                WriteEntry(nested, 0, "inside.bin", 4, 4, isDirectory: false);
                source.Write(partitionOffset + NestedDirectorySector * 2048L, nested);
                source.Write(partitionOffset + 4 * 2048L, [0x11, 0x22, 0x33, 0x44]);
                break;
            case Shape.EmptyDirectories:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false, right: 16);
                WriteEntry(root, 64, "EMPTYA", 0, 0, isDirectory: true, right: 32);
                WriteEntry(root, 128, "EMPTYB", 0, 0, isDirectory: true);
                break;
            case Shape.MalformedTree:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false, right: ushort.MaxValue);
                break;
            case Shape.OutOfRangeDirectory:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false, right: 16);
                WriteEntry(root, 64, "BADDIR", 0x100, 2048, isDirectory: true);
                break;
            case Shape.CyclicDirectoryExtent:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false, right: 16);
                WriteEntry(root, 64, "DIR", NestedDirectorySector, 32, isDirectory: true);
                var self = new byte[32];
                WriteEntry(self, 0, "SELF", NestedDirectorySector, 32, isDirectory: true);
                source.Write(partitionOffset + NestedDirectorySector * 2048L, self);
                break;
            case Shape.EmptyName:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false);
                root[13] = 0;
                break;
            case Shape.MissingDefaultExecutable:
                WriteEntry(root, 0, "readme.txt", 4, 4, isDirectory: false);
                break;
            case Shape.InvalidOptionalHeaderCount:
            case Shape.ExecutionInfoOutsideExecutable:
            case Shape.NoExecutionMetadata:
            case Shape.InvalidXexMagic:
                WriteEntry(root, 0, "default.xex", DefaultExecutableSector, 64, isDirectory: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        source.Write(partitionOffset + RootSector * 2048L, root);
        source.Write(partitionOffset + DefaultExecutableSector * 2048L, MakeExecutable(shape));
        return source;
    }

    private static byte[] MakeExecutable(Shape shape)
    {
        var executable = new byte[64];
        (shape == Shape.InvalidXexMagic ? "BAD!"u8 : "XEX2"u8).CopyTo(executable);
        var optionalHeaderCount = shape == Shape.InvalidOptionalHeaderCount ? 4097u : 1u;
        BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(20), optionalHeaderCount);
        BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(24),
            shape == Shape.NoExecutionMetadata ? 0x00040007u : 0x00040006u);
        BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(28),
            shape == Shape.ExecutionInfoOutsideExecutable ? 63u : 40u);
        if (shape != Shape.InvalidOptionalHeaderCount && shape != Shape.ExecutionInfoOutsideExecutable)
        {
            BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(40), 0x11223344);
            BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(44), 0x55667788);
            BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(48), 0x99AABBCC);
            BinaryPrimitives.WriteUInt32BigEndian(executable.AsSpan(52), 0xA1B2C3D4);
            executable[56] = 0x57;
            executable[57] = 0xA3;
            executable[58] = 2;
            executable[59] = 3;
        }
        return executable;
    }

    private static void WriteEntry(byte[] table, int offset, string name, uint sector, uint size,
        bool isDirectory, ushort left = 0, ushort right = 0)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(offset), left);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(offset + 2), right);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(offset + 4), sector);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(offset + 8), size);
        table[offset + 12] = isDirectory ? (byte)0x10 : (byte)0;
        table[offset + 13] = checked((byte)nameBytes.Length);
        nameBytes.CopyTo(table, offset + 14);
    }

    private enum Shape
    {
        Simple,
        NestedFile,
        EmptyDirectories,
        MalformedTree,
        OutOfRangeDirectory,
        CyclicDirectoryExtent,
        EmptyName,
        MissingDefaultExecutable,
        InvalidXexMagic,
        InvalidOptionalHeaderCount,
        ExecutionInfoOutsideExecutable,
        NoExecutionMetadata
    }
}
