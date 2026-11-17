using System.Buffers.Binary;
using System.Security.Cryptography;
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
            IsoLibrary.OpenAsync(Path.Combine(invalid, "bad.iso"), null, null, false, cache, NullLogger.Instance,
                isoMetadataLayout: false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsoLibrary.OpenAsync(null, null, null, false, cache, NullLogger.Instance, isoMetadataLayout: false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsoLibrary.OpenAsync(good, Path.GetDirectoryName(good), null, false, cache, NullLogger.Instance,
                isoMetadataLayout: false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsoLibrary.OpenAsync(null, Path.GetDirectoryName(good), "Ambiguous", false, cache, NullLogger.Instance,
                isoMetadataLayout: false));
    }

    [Fact]
    public async Task OpenAsync_DefaultMetadataLayoutUsesDisplayTitleAndPreservesIsoPayloadWithoutGodCache()
    {
        using var temp = new TemporaryDirectory();
        const uint titleId = 0x10203040;
        const uint mediaId = 0xA0B0C0D0;
        var isoPath = WriteIso(temp.Path, "original-name.iso", titleId, mediaId, 1, 2, 0x5A,
            displayTitle: "Actual display title");
        var cache = Path.Combine(temp.Path, "unused-cache");

        using var library = await IsoLibrary.OpenAsync(
            isoPath, null, null, false, cache, NullLogger.Instance);

        var game = Assert.Single(library.Games);
        Assert.Equal("Actual display title", game.Title);
        Assert.Equal("Games/ISOs/Actual display title [10203040][Disc 1 of 2][A0B0C0D0]/default.xex", game.LaunchPath);
        Assert.Equal((byte)2, game.DiscCount);
        Assert.Empty(library.Excluded);
        Assert.Empty(library.Warnings);
        Assert.Null(game.GodPackage);
        Assert.Null(library.Root.Find("Content"));
        Assert.False(Directory.Exists(cache));
        Assert.Equal(new byte[] { 0x5A }, await ReadPayloadAt(library, game.LaunchPath));
        Assert.Equal(new byte[] { 0x5A }, await ReadPayload(library, "ISOs/Actual display title [10203040][Disc 1 of 2][A0B0C0D0]"));
    }

    [Fact]
    public async Task OpenAsync_SingleIsoTitleOverrideUsesMetadataLayoutAndManualTitle()
    {
        using var temp = new TemporaryDirectory();
        var iso = WriteIso(temp.Path, "disc.iso", 0x12345678, 0xABCDEF01, 1, 1, 0x5A,
            displayTitle: "Embedded title");

        using var library = await IsoLibrary.OpenAsync(
            iso, null, "Manual title", false, Path.Combine(temp.Path, "cache"), NullLogger.Instance);

        var game = Assert.Single(library.Games);
        Assert.True(library.IsSingleIso);
        Assert.Equal("Manual title", game.Title);
        Assert.Equal("Games/ISOs/Manual title [12345678][ABCDEF01]/default.xex", game.LaunchPath);
        Assert.Equal(new byte[] { 0x5A }, await ReadPayloadAt(library, game.LaunchPath));
        Assert.Empty(library.Warnings);
    }

    [Fact]
    public async Task OpenAsync_EncryptedLzxTitleDrivesLayoutWithoutChangingExecutableBytes()
    {
        using var temp = new TemporaryDirectory();
        const string title = "Synthetic café 世界";
        var executable = XexTitleFixtures.CreateProtectedExecutable(title, compression: 2);
        var iso = WriteIso(temp.Path, "unrelated-filename.iso", XexTitleFixtures.DefaultTitleId,
            XexTitleFixtures.DefaultMediaId, 2, 3, 0x83, executableOverride: executable);
        var cache = Path.Combine(temp.Path, "unused-cache");

        using var library = await IsoLibrary.OpenAsync(
            iso, null, null, false, cache, NullLogger.Instance);

        var game = Assert.Single(library.Games);
        Assert.Equal(title, game.Title);
        Assert.Equal("Games/ISOs/Synthetic café 世界 [A1B2C3D4][Disc 2 of 3][11223344]/default.xex",
            game.LaunchPath);
        Assert.Equal(new byte[] { 0x83 }, await ReadPayloadAt(library, game.LaunchPath));
        var file = Assert.IsAssignableFrom<VirtualFile>(library.Root.Find(game.LaunchPath));
        var exported = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(0, exported);
        Assert.Equal(executable, exported);
        Assert.Empty(library.Warnings);
        Assert.Empty(library.Excluded);
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public async Task OpenAsync_MissingTitleMetadataFallsBackAndReportsLoggerAndLibraryWarning()
    {
        using var temp = new TemporaryDirectory();
        var isoPath = WriteIso(temp.Path, "filename fallback.iso", 1, 2, 1, 1, 0x5B);
        var logger = new RecordingLogger();

        using var library = await IsoLibrary.OpenAsync(
            isoPath, null, null, false, Path.Combine(temp.Path, "cache"), logger);

        var game = Assert.Single(library.Games);
        Assert.Equal("filename fallback", game.Title);
        Assert.Equal("Games/ISOs/filename fallback [00000001][00000002]/default.xex", game.LaunchPath);
        var warning = Assert.Single(library.Warnings);
        Assert.Equal("filename fallback.iso", warning.Source);
        Assert.Contains("filename", warning.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logger.Warnings, message => message.Contains("filename fallback.iso", StringComparison.Ordinal));
        Assert.Empty(library.Excluded);
    }

    [Fact]
    public async Task OpenAsync_SanitizesInvalidFatCharactersInDisplayTitle()
    {
        using var temp = new TemporaryDirectory();
        const string title = "A/B:C*D?E\"F<G>H|I\nZ";
        var iso = WriteIso(temp.Path, "sanitize.iso", 0x01020304, 0x05060708, 1, 1, 0x5C,
            displayTitle: title);

        using var library = await IsoLibrary.OpenAsync(
            iso, null, null, false, Path.Combine(temp.Path, "cache"), NullLogger.Instance);

        var game = Assert.Single(library.Games);
        Assert.Equal(title, game.Title);
        Assert.Equal("Games/ISOs/A_B_C_D_E_F_G_H_I_Z [01020304][05060708]/default.xex", game.LaunchPath);
        Assert.Contains(library.Warnings, warning => warning.Source == "sanitize.iso" &&
            warning.Reason.Contains("normalized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenAsync_UsesUntitledWhenSanitizedDisplayTitleIsEmpty()
    {
        using var temp = new TemporaryDirectory();
        var iso = WriteIso(temp.Path, "empty-title.iso", 0x01020304, 0x05060709, 1, 1, 0x5C,
            displayTitle: " . ");

        using var library = await IsoLibrary.OpenAsync(
            iso, null, null, false, Path.Combine(temp.Path, "cache"), NullLogger.Instance);

        Assert.Equal(".", Assert.Single(library.Games).Title);
        Assert.Equal("Games/ISOs/Untitled [01020304][05060709]/default.xex",
            Assert.Single(library.Games).LaunchPath);
    }

    [Fact]
    public async Task OpenAsync_TruncatesDisplayTitleAtRuneBoundaryAndAllowsExactly160Utf16Units()
    {
        using var temp = new TemporaryDirectory();
        WriteIso(temp.Path, "one.iso", 1, 11, 1, 1, 0x5D,
            displayTitle: new string('A', 159) + "😀");
        WriteIso(temp.Path, "two.iso", 2, 12, 1, 1, 0x5E,
            displayTitle: new string('B', 158) + "😀");

        using var library = await IsoLibrary.OpenAsync(
            null, temp.Path, null, false, Path.Combine(temp.Path, "cache"), NullLogger.Instance);

        var first = Assert.Single(library.Games, game => game.Source == "one.iso");
        var second = Assert.Single(library.Games, game => game.Source == "two.iso");
        var firstComponent = first.LaunchPath["Games/ISOs/".Length..].Split(" [", 2)[0];
        var secondComponent = second.LaunchPath["Games/ISOs/".Length..].Split(" [", 2)[0];
        Assert.Equal(159, firstComponent.Length);
        Assert.Equal(new string('A', 159), firstComponent);
        Assert.Equal(160, secondComponent.Length);
        Assert.Equal(new string('B', 158) + "😀", secondComponent);
        Assert.False(char.IsHighSurrogate(firstComponent[^1]));
        Assert.True(char.IsHighSurrogate(secondComponent[^2]));
        Assert.True(char.IsLowSurrogate(secondComponent[^1]));
    }

    [Fact]
    public async Task OpenAsync_DiscAndMediaIdentitySeparateDefaultLayoutPaths()
    {
        using var temp = new TemporaryDirectory();
        WriteIso(temp.Path, "disc-one.iso", 7, 9, 1, 2, 0x61, displayTitle: "Multi Disc");
        WriteIso(temp.Path, "disc-two.iso", 7, 9, 2, 2, 0x62, displayTitle: "Multi Disc");
        WriteIso(temp.Path, "other-media.iso", 7, 10, 1, 1, 0x63, displayTitle: "Multi Disc");

        using var library = await IsoLibrary.OpenAsync(
            null, temp.Path, null, false, Path.Combine(temp.Path, "cache"), NullLogger.Instance);

        Assert.Equal(3, library.Games.Count);
        Assert.Equal(3, library.Games.Select(game => game.LaunchPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(library.Games, game => game.LaunchPath.EndsWith("Multi Disc [00000007][Disc 1 of 2][00000009]/default.xex", StringComparison.Ordinal));
        Assert.Contains(library.Games, game => game.LaunchPath.EndsWith("Multi Disc [00000007][Disc 2 of 2][00000009]/default.xex", StringComparison.Ordinal));
        Assert.Contains(library.Games, game => game.LaunchPath.EndsWith("Multi Disc [00000007][0000000A]/default.xex", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 0x61 }, await ReadPayloadAt(library, Assert.Single(library.Games, game => game.Source == "disc-one.iso").LaunchPath));
        Assert.Equal(new byte[] { 0x62 }, await ReadPayloadAt(library, Assert.Single(library.Games, game => game.Source == "disc-two.iso").LaunchPath));
        Assert.Equal(new byte[] { 0x63 }, await ReadPayloadAt(library, Assert.Single(library.Games, game => game.Source == "other-media.iso").LaunchPath));
    }

    [Theory]
    [InlineData(0u, 0u, (byte)1, (byte)1, "Optional")]
    [InlineData(1u, 0u, (byte)1, (byte)1, "Optional [00000001]")]
    [InlineData(0u, 2u, (byte)1, (byte)1, "Optional [00000002]")]
    [InlineData(0u, 0u, (byte)2, (byte)3, "Optional [Disc 2 of 3]")]
    [InlineData(1u, 2u, (byte)0, (byte)0, "Optional [00000001][00000002]")]
    [InlineData(1u, 2u, (byte)1, (byte)0, "Optional [00000001][00000002]")]
    [InlineData(1u, 2u, (byte)0, (byte)2, "Optional [00000001][00000002]")]
    public async Task OpenAsync_MetadataSuffixOmitsUnavailableGroupsAndEmptyBrackets(uint titleId, uint mediaId,
        byte discNumber, byte discCount, string folder)
    {
        using var temp = new TemporaryDirectory();
        var iso = WriteIso(temp.Path, "unrelated.iso", titleId, mediaId, discNumber, discCount, 0x64,
            displayTitle: "Optional");

        using var library = await IsoLibrary.OpenAsync(
            iso, null, null, false, Path.Combine(temp.Path, "cache"), NullLogger.Instance);

        var game = Assert.Single(library.Games);
        Assert.Equal($"Games/ISOs/{folder}/default.xex", game.LaunchPath);
        Assert.Equal(new byte[] { 0x64 }, await ReadPayloadAt(library, game.LaunchPath));
    }

    [Fact]
    public async Task OpenAsync_DuplicateAndSanitizedCaseCollisionFoldersKeepIndependentGamesWithStablePaths()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        WriteIso(root, "01-exact.iso", 1, 11, 1, 1, 0x71, displayTitle: "Exact");
        WriteIso(root, "02-exact.iso", 1, 11, 1, 1, 0x72, displayTitle: "Exact");
        WriteIso(root, "07-exact.iso", 1, 11, 1, 1, 0x77, displayTitle: "Exact");
        WriteIso(root, "03-slash.iso", 2, 22, 1, 1, 0x73, displayTitle: "A/B");
        WriteIso(root, "04-question.iso", 2, 22, 1, 1, 0x74, displayTitle: "A?B");
        WriteIso(root, "05-lower.iso", 3, 33, 1, 1, 0x75, displayTitle: "same");
        WriteIso(root, "06-upper.iso", 3, 33, 1, 1, 0x76, displayTitle: "SAME");
        var logger = new RecordingLogger();
        var cache = Path.Combine(temp.Path, "cache");

        Dictionary<string, string> paths;
        using (var library = await IsoLibrary.OpenAsync(null, root, null, false, cache, logger))
        {
            Assert.Equal(7, library.Games.Count);
            Assert.Empty(library.Excluded);
            Assert.Equal(4, library.Warnings.Count(warning => warning.Reason.Contains(
                "Duplicate ISO metadata folder", StringComparison.Ordinal)));
            Assert.Equal(4, logger.Warnings.Count(message =>
                message.Contains("Duplicate ISO metadata folder", StringComparison.Ordinal)));
            Assert.Equal(library.Games.Count, library.Games.Select(game => game.LaunchPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var firstExact = Assert.Single(library.Games, game => game.Source == "01-exact.iso");
            var secondExact = Assert.Single(library.Games, game => game.Source == "02-exact.iso");
            var thirdExact = Assert.Single(library.Games, game => game.Source == "07-exact.iso");
            Assert.Equal("Games/ISOs/Exact [00000001][0000000B] (1)/default.xex", firstExact.LaunchPath);
            Assert.Equal("Games/ISOs/Exact [00000001][0000000B] (2)/default.xex", secondExact.LaunchPath);
            Assert.Equal("Games/ISOs/Exact [00000001][0000000B] (3)/default.xex", thirdExact.LaunchPath);
            Assert.DoesNotContain('[', firstExact.LaunchPath["Games/ISOs/Exact [00000001][0000000B]".Length..]);
            Assert.DoesNotContain('[', secondExact.LaunchPath["Games/ISOs/Exact [00000001][0000000B]".Length..]);
            Assert.DoesNotContain('[', thirdExact.LaunchPath["Games/ISOs/Exact [00000001][0000000B]".Length..]);
            foreach (var game in library.Games)
                Assert.Equal(new byte[] { PayloadFor(game.Source) }, await ReadPayloadAt(library, game.LaunchPath));
            paths = library.Games.ToDictionary(game => game.Source, game => game.LaunchPath, StringComparer.Ordinal);
        }

        using var repeated = await IsoLibrary.OpenAsync(null, root, null, false, cache, NullLogger.Instance);
        Assert.Equal(paths, repeated.Games.ToDictionary(game => game.Source, game => game.LaunchPath, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OpenAsync_MetadataOptionChangesSnapshotAndLayoutIsStable()
    {
        using var temp = new TemporaryDirectory();
        var legacyRelativePath = "ISOs/Snapshot Title [11112222][33334444]";
        var root = Path.Combine(temp.Path, "library-root");
        WriteIso(root, legacyRelativePath + ".iso", 0x11112222, 0x33334444, 1, 1, 0x77,
            displayTitle: "Snapshot Title");
        var cache = Path.Combine(temp.Path, "cache");

        string metadataSnapshot;
        string metadataPath;
        using (var metadata = await IsoLibrary.OpenAsync(null, root, null, false, cache, NullLogger.Instance))
        {
            metadataSnapshot = metadata.Snapshot;
            metadataPath = Assert.Single(metadata.Games).LaunchPath;
        }
        string legacySnapshot;
        using (var legacy = await IsoLibrary.OpenAsync(null, root, null, false, cache, NullLogger.Instance,
                   isoMetadataLayout: false))
        {
            legacySnapshot = legacy.Snapshot;
            Assert.Equal(metadataPath, Assert.Single(legacy.Games).LaunchPath);
        }
        using var repeated = await IsoLibrary.OpenAsync(null, root, null, false, cache, NullLogger.Instance);

        Assert.NotEqual(metadataSnapshot, legacySnapshot);
        Assert.Equal(metadataPath, Assert.Single(repeated.Games).LaunchPath);
        Assert.Equal(metadataSnapshot, repeated.Snapshot);
    }

    [Fact]
    public async Task OpenAsync_DuplicateGodPackageKeepsBothIsoViewsAndCleansDuplicateCache()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        WriteIso(root, "a.iso", 0x11111111, 0xAAAA0001, 1, 1, 0x81, displayTitle: "Same GOD identity");
        WriteIso(root, "b.iso", 0x11111111, 0xAAAA0001, 1, 1, 0x82, displayTitle: "Same GOD identity");
        var cache = Path.Combine(temp.Path, "cache");
        var logger = new RecordingLogger();

        using (var library = await IsoLibrary.OpenAsync(null, root, null, true, cache, logger))
        {
            Assert.Equal(2, library.Games.Count);
            Assert.Empty(library.Excluded);
            Assert.Single(library.Games, game => game.GodPackage is not null);
            Assert.Single(library.Games, game => game.GodPackage is null);
            Assert.Single(Directory.GetFiles(cache, "*.hashes"));
            Assert.Single(library.Warnings, warning => warning.Reason.Contains(
                "Duplicate GOD package path", StringComparison.Ordinal));
            Assert.Contains(logger.Warnings, message => message.Contains(
                "Duplicate GOD package path", StringComparison.Ordinal));
            Assert.Equal(new byte[] { 0x81 }, await ReadPayloadAt(library,
                Assert.Single(library.Games, game => game.Source == "a.iso").LaunchPath));
            Assert.Equal(new byte[] { 0x82 }, await ReadPayloadAt(library,
                Assert.Single(library.Games, game => game.Source == "b.iso").LaunchPath));
        }

        Assert.Empty(Directory.GetFiles(cache, "*.hashes"));
    }

    [Fact]
    public async Task OpenAsync_SingleIsoHonorsDefaultAndOverrideTitlesAndDirectoryCardinality()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "isos");
        var iso = WriteIso(root, "original.iso", 1, 2, 1, 1, 0x56);
        var cache = Path.Combine(temp.Path, "cache");
        using (var library = await IsoLibrary.OpenAsync(iso, null, null, false, cache, NullLogger.Instance,
                   isoMetadataLayout: false))
        {
            Assert.True(library.IsSingleIso);
            Assert.Equal("Games/original/default.xex", Assert.Single(library.Games).LaunchPath);
            Assert.Equal(new byte[] { 0x56 }, await ReadPayload(library, "original"));
        }
        using (var library = await IsoLibrary.OpenAsync(root, null, "Custom", false, cache, NullLogger.Instance,
                   isoMetadataLayout: false))
        {
            Assert.True(library.IsSingleIso);
            Assert.Equal("Games/Custom/default.xex", Assert.Single(library.Games).LaunchPath);
            Assert.Equal(new byte[] { 0x56 }, await ReadPayload(library, "Custom"));
        }
        var empty = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(empty);
        WriteIso(empty, "nested/only.iso", 3, 4, 1, 1, 0x57);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IsoLibrary.OpenAsync(empty, null, null, false, cache, NullLogger.Instance, isoMetadataLayout: false));
        WriteIso(root, "another.iso", 3, 4, 1, 1, 0x57);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsoLibrary.OpenAsync(root, null, null, false, cache, NullLogger.Instance, isoMetadataLayout: false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsoLibrary.OpenAsync(iso, null, "bad/title", false, cache, NullLogger.Instance, isoMetadataLayout: false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsoLibrary.OpenAsync(iso, null, @"bad\title", false, cache, NullLogger.Instance, isoMetadataLayout: false));
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
        using (var library = await IsoLibrary.OpenAsync(iso, null, "One", false, cache, NullLogger.Instance,
                   isoMetadataLayout: false))
            baseline = library.Snapshot;
        using (var renamed = await IsoLibrary.OpenAsync(iso, null, "Two", false, cache, NullLogger.Instance,
                   isoMetadataLayout: false))
            Assert.NotEqual(baseline, renamed.Snapshot);
        using var withGod = await IsoLibrary.OpenAsync(iso, null, "One", true, cache, NullLogger.Instance,
            isoMetadataLayout: false);
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
        IsoLibrary.OpenAsync(null, root, null, god, cache, logger ?? NullLogger.Instance,
            isoMetadataLayout: false);

    private static async Task<byte[]> ReadPayload(IsoLibrary library, string gamePath)
    {
        var file = Assert.IsAssignableFrom<VirtualFile>(library.Root.Find($"Games/{gamePath}/payload.bin"));
        var bytes = new byte[1];
        await file.ReadExactlyAsync(0, bytes);
        return bytes;
    }

    private static async Task<byte[]> ReadPayloadAt(IsoLibrary library, string launchPath)
    {
        var payloadPath = launchPath[..^"default.xex".Length] + "payload.bin";
        var file = Assert.IsAssignableFrom<VirtualFile>(library.Root.Find(payloadPath));
        var bytes = new byte[1];
        await file.ReadExactlyAsync(0, bytes);
        return bytes;
    }

    private static byte PayloadFor(string source) => source switch
    {
        "01-exact.iso" => 0x71,
        "02-exact.iso" => 0x72,
        "03-slash.iso" => 0x73,
        "04-question.iso" => 0x74,
        "05-lower.iso" => 0x75,
        "06-upper.iso" => 0x76,
        "07-exact.iso" => 0x77,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown fixture source.")
    };

    private static string WriteIso(string root, string relative, uint title, uint media,
        byte disc, byte discCount, byte payload, bool missingXex = false, string? displayTitle = null,
        byte[]? executableOverride = null)
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
        var executable = executableOverride ?? (displayTitle is null
            ? CreateLegacyExecutable(title, media, disc, discCount)
            : XexTitleFixtures.CreateExecutable(title, XexTitleFixtures.CreateXdbf((1, displayTitle)),
                mediaId: media, discNumber: disc, discCount: discCount));
        WriteEntry(table, missingXex ? "readme.txt" : "default.xex", 3,
            checked((uint)executable.Length), 16);
        WriteEntry(table[64..], "payload.bin", 4, 1, 0);
        executable.CopyTo(image, 3 * 2048);
        image[4 * 2048] = payload;
        File.WriteAllBytes(path, image);
        File.SetLastWriteTimeUtc(path, FixedTime);
        return path;
    }

    private static byte[] CreateLegacyExecutable(uint title, uint media, byte disc, byte discCount)
    {
        var xex = new byte[64];
        "XEX2"u8.CopyTo(xex);
        BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(24), 0x00040006);
        BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(28), 40);
        BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(40), media);
        BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(52), title);
        xex[56] = 0x57;
        xex[57] = 0xA3;
        xex[58] = disc;
        xex[59] = discCount;
        return xex;
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
