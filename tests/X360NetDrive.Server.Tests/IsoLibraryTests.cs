using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X360NetDrive.Server.Library;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class IsoLibraryTests
{
    [Fact]
    public async Task OpenAsync_RecursiveLibraryPreservesPathsDiscsAndIndependentReadsWithoutGod()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        WriteIso(root, "first/disc.iso", 0x11111111, 0xAAAA0001, 1, 2, 0x31);
        WriteIso(root, "first/disc2.ISO", 0x11111111, 0xAAAA0002, 2, 2, 0x32);
        WriteIso(root, "second/disc.iso", 0x22222222, 0xBBBB0001, 1, 1, 0x33);
        File.WriteAllText(Path.Combine(root, "not-an-iso.txt"), "not an ISO");
        var cache = Path.Combine(temp.Path, "unused-cache");

        using var library = await OpenRoot(root, cache);

        Assert.False(library.IsSingleIso);
        Assert.Empty(library.Excluded);
        Assert.Equal(3, library.Games.Count);
        var first = Assert.Single(library.Games, game => game.Source == "first/disc.iso");
        var secondDisc = Assert.Single(library.Games, game => game.Source == "first/disc2.ISO");
        var other = Assert.Single(library.Games, game => game.Source == "second/disc.iso");
        Assert.Equal("Games/first/disc/default.xex", first.LaunchPath);
        Assert.Equal("Games/first/disc2/default.xex", secondDisc.LaunchPath);
        Assert.Equal("Games/second/disc/default.xex", other.LaunchPath);
        Assert.Equal("11111111", first.TitleId);
        Assert.Equal("AAAA0001", first.MediaId);
        Assert.Equal((byte)1, first.DiscNumber);
        Assert.Equal((byte)2, first.DiscCount);
        Assert.Equal((byte)2, secondDisc.DiscNumber);
        Assert.Null(first.GodPackage);
        Assert.Null(library.Root.Find("Content"));
        Assert.False(Directory.Exists(cache));
        Assert.Equal(new byte[] { 0x31 }, await ReadPayload(library, "first/disc"));
        Assert.Equal(new byte[] { 0x32 }, await ReadPayload(library, "first/disc2"));
        Assert.Equal(new byte[] { 0x33 }, await ReadPayload(library, "second/disc"));
        Assert.IsAssignableFrom<VirtualFile>(library.Root.Find(first.LaunchPath));
    }

    [Fact]
    public async Task OpenAsync_ExcludesBadImagesAndMissingXexWhileServingValidImage()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        WriteIso(root, "good.iso", 1, 2, 1, 1, 0x45);
        Directory.CreateDirectory(Path.Combine(root, "bad"));
        File.WriteAllBytes(Path.Combine(root, "bad", "broken.iso"), new byte[128]);
        WriteIso(root, "bad/no-xex.iso", 3, 4, 1, 1, 0x46, missingXex: true);
        var logger = new RecordingLogger();

        using var library = await OpenRoot(root, Path.Combine(temp.Path, "cache"), logger: logger);

        Assert.Equal("good.iso", Assert.Single(library.Games).Source);
        Assert.Equal(new byte[] { 0x45 }, await ReadPayload(library, "good"));
        Assert.Equal(2, library.Excluded.Count);
        foreach (var path in new[] { "bad/broken.iso", "bad/no-xex.iso" })
        {
            var excluded = Assert.Single(library.Excluded, item => item.Source == path);
            Assert.False(string.IsNullOrWhiteSpace(excluded.Reason));
            Assert.Contains(logger.Warnings, warning => warning.Contains(Path.GetFileName(path),
                StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsEmptyInvalidAndConflictingInputs()
    {
        using var temp = new TemporaryDirectory();
        var empty = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(empty);
        var invalid = Path.Combine(temp.Path, "invalid");
        Directory.CreateDirectory(invalid);
        File.WriteAllBytes(Path.Combine(invalid, "bad.iso"), new byte[128]);
        var cache = Path.Combine(temp.Path, "cache");
        var missing = Path.Combine(temp.Path, "missing");
        var good = WriteIso(Path.Combine(temp.Path, "valid"), "good.iso", 1, 2, 1, 1, 0x40);

        var emptyError = await Assert.ThrowsAsync<InvalidDataException>(() => OpenRoot(empty, cache));
        Assert.Contains("No ISO", emptyError.Message, StringComparison.OrdinalIgnoreCase);
        var invalidError = await Assert.ThrowsAsync<InvalidDataException>(() => OpenRoot(invalid, cache));
        Assert.Contains("No supported ISO", invalidError.Message, StringComparison.OrdinalIgnoreCase);
        var missingError = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => OpenRoot(missing, cache));
        Assert.Contains(missing, missingError.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => OpenRoot(Path.Combine(invalid, "bad.iso"), cache));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IsoLibrary.OpenAsync(Path.Combine(invalid, "bad.iso"), null, null, false, cache, NullLogger.Instance));
        await Assert.ThrowsAsync<ArgumentException>(() => IsoLibrary.OpenAsync(null, null, null, false, cache, NullLogger.Instance));
        await Assert.ThrowsAsync<ArgumentException>(() => IsoLibrary.OpenAsync(good, Path.GetDirectoryName(good), null, false, cache, NullLogger.Instance));
        await Assert.ThrowsAsync<ArgumentException>(() => IsoLibrary.OpenAsync(null, Path.GetDirectoryName(good), "Ambiguous", false, cache, NullLogger.Instance));
    }

    [Fact]
    public async Task OpenAsync_SingleIsoHonorsDefaultAndOverrideTitlesAndDirectoryCardinality()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        var iso = WriteIso(root, "original.iso", 1, 2, 1, 1, 0x56);
        var cache = Path.Combine(temp.Path, "cache");
        using (var library = await IsoLibrary.OpenAsync(iso, null, null, false, cache, NullLogger.Instance))
        {
            Assert.True(library.IsSingleIso);
            Assert.Equal("Games/original/default.xex", Assert.Single(library.Games).LaunchPath);
            Assert.Equal(new byte[] { 0x56 }, await ReadPayload(library, "original"));
        }
        using (var library = await IsoLibrary.OpenAsync(root, null, "Custom", false, cache, NullLogger.Instance))
        {
            Assert.True(library.IsSingleIso);
            Assert.Equal("Games/Custom/default.xex", Assert.Single(library.Games).LaunchPath);
            Assert.Equal(new byte[] { 0x56 }, await ReadPayload(library, "Custom"));
        }
        var empty = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(empty);
        WriteIso(empty, "nested/only.iso", 3, 4, 1, 1, 0x57);
        await Assert.ThrowsAsync<InvalidDataException>(() => IsoLibrary.OpenAsync(empty, null, null, false, cache, NullLogger.Instance));
        WriteIso(root, "another.iso", 3, 4, 1, 1, 0x57);
        await Assert.ThrowsAsync<ArgumentException>(() => IsoLibrary.OpenAsync(root, null, null, false, cache, NullLogger.Instance));
        await Assert.ThrowsAsync<ArgumentException>(() => IsoLibrary.OpenAsync(iso, null, "bad/title", false, cache, NullLogger.Instance));
        await Assert.ThrowsAsync<ArgumentException>(() => IsoLibrary.OpenAsync(iso, null, @"bad\title", false, cache, NullLogger.Instance));
    }

    [Fact]
    public async Task OpenAsync_SnapshotIsStableAcrossCreationOrderAndChangesWithMembershipAndMetadata()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        var cache = Path.Combine(temp.Path, "cache");
        var b = WriteIso(root, "b.iso", 2, 3, 1, 1, 0x62);
        var a = WriteIso(root, "a.iso", 1, 2, 1, 1, 0x61);
        string original;
        using (var library = await OpenRoot(root, cache))
        {
            original = library.Snapshot;
            Assert.Matches("^[0-9a-fA-F]{24}$", original);
        }
        File.Delete(a);
        File.Delete(b);
        WriteIso(root, "a.iso", 1, 2, 1, 1, 0x61);
        WriteIso(root, "b.iso", 2, 3, 1, 1, 0x62);
        using (var reordered = await OpenRoot(root, cache))
            Assert.Equal(original, reordered.Snapshot);
        WriteIso(root, "c.iso", 3, 4, 1, 1, 0x63);
        string added;
        using (var expanded = await OpenRoot(root, cache))
        {
            added = expanded.Snapshot;
            Assert.NotEqual(original, added);
        }
        File.SetLastWriteTimeUtc(a, FixedTime.AddMinutes(2));
        using var changed = await OpenRoot(root, cache);
        Assert.NotEqual(added, changed.Snapshot);
    }

    [Fact]
    public async Task OpenAsync_SnapshotChangesWithTitleAndGodOption()
    {
        using var temp = new TemporaryDirectory();
        var iso = WriteIso(temp.Path, "game.iso", 1, 2, 1, 1, 0x71);
        var cache = Path.Combine(temp.Path, "cache");
        string baseline;
        using (var library = await IsoLibrary.OpenAsync(iso, null, "One", false, cache, NullLogger.Instance))
            baseline = library.Snapshot;
        using (var renamed = await IsoLibrary.OpenAsync(iso, null, "Two", false, cache, NullLogger.Instance))
            Assert.NotEqual(baseline, renamed.Snapshot);
        using var withGod = await IsoLibrary.OpenAsync(iso, null, "One", true, cache, NullLogger.Instance);
        Assert.NotEqual(baseline, withGod.Snapshot);
    }

    [Fact]
    public async Task OpenAsync_MergesGodPackagesAndReleasesCachesAndSourcesOnDispose()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        WriteIso(root, "one.iso", 0x11111111, 0xAAAA0001, 1, 1, 0x81);
        WriteIso(root, "two.iso", 0x22222222, 0xBBBB0002, 1, 1, 0x82);
        var cache = Path.Combine(temp.Path, "cache");
        var library = await OpenRoot(root, cache, god: true);
        try
        {
            Assert.Empty(library.Excluded);
            Assert.Equal(2, library.Games.Count);
            foreach (var path in new[] {
                "Content/0000000000000000/11111111/00007000/AAAA0001",
                "Content/0000000000000000/22222222/00007000/BBBB0002" })
            {
                Assert.Contains(library.Games, game => game.GodPackage == path);
                var header = Assert.IsAssignableFrom<VirtualFile>(library.Root.Find(path));
                var magic = new byte[4];
                await header.ReadExactlyAsync(0, magic);
                Assert.Equal("LIVE", Encoding.ASCII.GetString(magic));
            }
            Assert.Equal(2, Directory.GetFiles(cache, "*.hashes").Length);
            var file = Assert.IsAssignableFrom<VirtualFile>(library.Root.Find("Games/one/payload.bin"));
            var payload = new byte[1];
            await file.ReadExactlyAsync(0, payload);
            Assert.Equal(new byte[] { 0x81 }, payload);
            library.Dispose();
            Assert.Empty(Directory.GetFiles(cache, "*.hashes"));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await file.ReadExactlyAsync(0, payload));
        }
        finally
        {
            library.Dispose();
        }
    }

    [Fact]
    public async Task OpenAsync_ExcludesDuplicateGodPackagePathInsteadOfOverwriting()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        WriteIso(root, "a.iso", 0x11111111, 0xAAAA0001, 1, 1, 0x91);
        WriteIso(root, "b.iso", 0x11111111, 0xAAAA0001, 1, 1, 0x92);
        var logger = new RecordingLogger();
        using var library = await OpenRoot(root, Path.Combine(temp.Path, "cache"), god: true, logger: logger);

        var excluded = Assert.Single(library.Excluded);
        Assert.Contains(excluded.Source, new[] { "a.iso", "b.iso" });
        Assert.False(string.IsNullOrWhiteSpace(excluded.Reason));
        Assert.Contains(logger.Warnings, warning => warning.Contains(excluded.Source, StringComparison.OrdinalIgnoreCase));
        var path = "Content/0000000000000000/11111111/00007000/AAAA0001";
        Assert.IsAssignableFrom<VirtualFile>(library.Root.Find(path));
        Assert.Single(library.Games, game => game.GodPackage == path);
    }

    private static readonly DateTime FixedTime = new(2022, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    private static Task<IsoLibrary> OpenRoot(string root, string cache, bool god = false, ILogger? logger = null) =>
        IsoLibrary.OpenAsync(null, root, null, god, cache, logger ?? NullLogger.Instance);

    private static async Task<byte[]> ReadPayload(IsoLibrary library, string gamePath)
    {
        var file = Assert.IsAssignableFrom<VirtualFile>(library.Root.Find($"Games/{gamePath}/payload.bin"));
        var bytes = new byte[1];
        await file.ReadExactlyAsync(0, bytes);
        return bytes;
    }

    private static string WriteIso(string root, string relative, uint title, uint media,
        byte disc, byte discCount, byte payload, bool missingXex = false)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new byte[0x12000];
        var descriptor = image.AsSpan(0x10000, 2048);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(descriptor);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(descriptor[0x7EC..]);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], 128);
        var table = image.AsSpan(2048, 128);
        WriteEntry(table, missingXex ? "readme.txt" : "default.xex", 3, 64, 16);
        WriteEntry(table[64..], "payload.bin", 4, 1, 0);
        var xex = image.AsSpan(3 * 2048, 64);
        "XEX2"u8.CopyTo(xex);
        BinaryPrimitives.WriteUInt32BigEndian(xex[20..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(xex[24..], 0x00040006);
        BinaryPrimitives.WriteUInt32BigEndian(xex[28..], 40);
        BinaryPrimitives.WriteUInt32BigEndian(xex[40..], media);
        BinaryPrimitives.WriteUInt32BigEndian(xex[52..], title);
        xex[56] = 0x57;
        xex[57] = 0xA3;
        xex[58] = disc;
        xex[59] = discCount;
        image[4 * 2048] = payload;
        File.WriteAllBytes(path, image);
        File.SetLastWriteTimeUtc(path, FixedTime);
        return path;
    }

    private static void WriteEntry(Span<byte> table, string name, uint sector, uint size, ushort right)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(table[2..], right);
        BinaryPrimitives.WriteUInt32LittleEndian(table[4..], sector);
        BinaryPrimitives.WriteUInt32LittleEndian(table[8..], size);
        var nameBytes = Encoding.Latin1.GetBytes(name);
        table[13] = checked((byte)nameBytes.Length);
        nameBytes.CopyTo(table[14..]);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
