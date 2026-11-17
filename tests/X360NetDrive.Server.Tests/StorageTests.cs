using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class StorageTests
{
    private static readonly DateTimeOffset Modified =
        new(2023, 5, 6, 7, 8, 10, TimeSpan.Zero);

    [Fact]
    public async Task SliceVirtualFile_ReadsFromSliceRelativeOffsetsAndStopsAtEof()
    {
        var source = new MemoryVirtualFile("source.bin", "0123456789"u8.ToArray(), Modified);
        var slice = new SliceVirtualFile("slice.bin", source, start: 3, length: 5);
        var buffer = new byte[8];

        var read = await slice.ReadAsync(1, buffer);

        Assert.Equal(4, read);
        Assert.Equal("4567", System.Text.Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal(0, await slice.ReadAsync(5, buffer));
        Assert.Equal(0, await slice.ReadAsync(50, buffer));
    }

    [Fact]
    public async Task SliceVirtualFile_RejectsNegativeOffsetsAndOutOfBoundsSlices()
    {
        var source = new MemoryVirtualFile("source.bin", "data"u8.ToArray(), Modified);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SliceVirtualFile("past-end.bin", source, start: 5, length: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SliceVirtualFile("too-long.bin", source, start: 2, length: 3));

        var slice = new SliceVirtualFile("slice.bin", source, start: 0, length: 4);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await slice.ReadAsync(-1, new byte[1]));
    }

    [Fact]
    public async Task VirtualReadStream_SeeksAndReadsAtRequestedPositions()
    {
        var file = new MemoryVirtualFile("source.bin", "abcdef"u8.ToArray(), Modified);
        using var stream = new VirtualReadStream(file);
        var buffer = new byte[3];

        Assert.Equal(2, stream.Seek(2, SeekOrigin.Begin));
        Assert.Equal(2, await stream.ReadAsync(buffer.AsMemory(0, 2)));
        Assert.Equal("cd", System.Text.Encoding.ASCII.GetString(buffer, 0, 2));
        Assert.Equal(5, stream.Seek(-1, SeekOrigin.End));
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'f', buffer[0]);
        Assert.Equal(0, await stream.ReadAsync(buffer));
        Assert.Equal(9, stream.Seek(3, SeekOrigin.Current));
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task VirtualFile_ReadExactlyThrowsWhenTheRequestedRangeCrossesEof()
    {
        var file = new MemoryVirtualFile("short.bin", "abc"u8.ToArray(), Modified);
        var buffer = new byte[4];

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await file.ReadExactlyAsync(0, buffer));

        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(buffer, 0, 3));
    }

    [Fact]
    public async Task VirtualDirectory_FindIsCaseInsensitiveAndRejectsTraversal()
    {
        var root = new VirtualDirectory("", Modified);
        var folder = new VirtualDirectory("Folder", Modified);
        folder.Add(new MemoryVirtualFile("ReadMe.txt", "contents"u8.ToArray(), Modified));
        root.Add(folder);

        Assert.Same(folder, root.Find("folder"));
        var file = Assert.IsType<MemoryVirtualFile>(root.Find("FOLDER/readme.TXT"));
        Assert.Equal(8, file.Length);
        var contents = new byte[8];
        await file.ReadExactlyAsync(0, contents);
        Assert.Equal("contents", System.Text.Encoding.ASCII.GetString(contents));
        Assert.Null(root.Find("../Folder/ReadMe.txt"));
        Assert.Null(root.Find("Folder\\ReadMe.txt"));
    }

    [Fact]
    public void VirtualDirectory_AddRejectsDuplicateAndInvalidNames()
    {
        var root = new VirtualDirectory("", Modified);
        root.Add(new MemoryVirtualFile("Game.bin", "one"u8.ToArray(), Modified));

        Assert.Throws<InvalidDataException>(() =>
            root.Add(new MemoryVirtualFile("game.BIN", "two"u8.ToArray(), Modified)));
        Assert.Throws<ArgumentException>(() =>
            root.Add(new MemoryVirtualFile("../escape", "data"u8.ToArray(), Modified)));
    }
}
