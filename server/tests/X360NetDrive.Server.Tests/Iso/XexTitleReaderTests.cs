using System.Buffers.Binary;
using System.Text;
using X360NetDrive.Server.Iso;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class XexTitleReaderTests
{
    [Fact]
    public async Task ReadAsync_PrefersEnglishXstrTitleOverFilenameAndOtherLanguages()
    {
        var xdbf = XexTitleFixtures.CreateXdbf(
            (9, "Translation Nine"),
            (0, "Fallback language zero"),
            (1, "English game title"),
            (2, "Translation Two"));
        var executable = XexTitleFixtures.CreateExecutable(XexTitleFixtures.DefaultTitleId, xdbf);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("misleading-filename.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal("English game title", result.Title);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ReadAsync_WithoutEnglishSelectsLowestLanguageWithTitle()
    {
        var xdbf = XexTitleFixtures.CreateXdbf((7, "Language seven"), (4, "Language four"), (2, null));
        var executable = XexTitleFixtures.CreateExecutable(XexTitleFixtures.DefaultTitleId, xdbf);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("fallback.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal("Language four", result.Title);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ReadAsync_BasicCompressionMapsDataAroundZeroFillBlocks()
    {
        var xdbf = XexTitleFixtures.CreateXdbf((1, "Compressed title"));
        var executable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, xdbf, compression: 1, zeroFillBeforeResource: 0x35);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("compressed.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal("Compressed title", result.Title);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ReadAsync_ResourceMustMatchRequestedEightDigitTitleId()
    {
        var executable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Correct title")),
            resourceTitleId: XexTitleFixtures.DefaultTitleId + 1);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("id-mismatch.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.Contains("A1B2C3D4", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_OverLimitOptionalHeaderCountReturnsWarning()
    {
        var overLimit = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Title")));
        BinaryPrimitives.WriteUInt32BigEndian(overLimit.AsSpan(20), 4097);

        await AssertMetadataWarningAsync(overLimit);
    }

    [Fact]
    public async Task ReadAsync_DuplicateOptionalHeaderKeyReturnsWarning()
    {
        var duplicateKey = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Title")));
        BinaryPrimitives.WriteUInt32BigEndian(duplicateKey.AsSpan(32), 0x2FF);

        await AssertMetadataWarningAsync(duplicateKey);
    }

    [Fact]
    public async Task ReadAsync_MissingRequiredImageBaseReturnsWarning()
    {
        var missingImageBase = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Title")));
        BinaryPrimitives.WriteUInt32BigEndian(missingImageBase.AsSpan(40), 0xDEADBEEF);

        await AssertMetadataWarningAsync(missingImageBase);
    }

    [Fact]
    public async Task ReadAsync_ResourceOutsideImageReturnsWarning()
    {
        var validResource = XexTitleFixtures.CreateXdbf((1, "Bounded title"));
        var outsideImage = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, validResource, resourceAddressAdjustment: 0x100000);

        await AssertMetadataWarningAsync(outsideImage);
    }

    [Fact]
    public async Task ReadAsync_ResourceTableOver64KiBReturnsWarning()
    {
        var validResource = XexTitleFixtures.CreateXdbf((1, "Bounded title"));
        var badTable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, validResource, declaredResourceTableSize: 65540);

        await AssertMetadataWarningAsync(badTable);
    }

    [Fact]
    public async Task ReadAsync_XdbfStringTableOutsideResourceReturnsWarning()
    {
        var badXdbfExtent = XexTitleFixtures.CreateXdbf((1, "Invalid extent"));
        BinaryPrimitives.WriteUInt32BigEndian(badXdbfExtent.AsSpan(24 + 10), uint.MaxValue);
        var badXdbf = XexTitleFixtures.CreateExecutable(XexTitleFixtures.DefaultTitleId, badXdbfExtent);

        await AssertMetadataWarningAsync(badXdbf);
    }

    [Fact]
    public async Task ReadAsync_FullSixtyFourKiBResourceTableStillFindsTitle()
    {
        var executable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId,
            XexTitleFixtures.CreateXdbf((1, "Maximum table title")),
            declaredResourceTableSize: 65524);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("maximum-table.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal("Maximum table title", result.Title);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ReadAsync_InvalidUtf8TitleReturnsWarning()
    {
        var invalidUtf8Table = XexTitleFixtures.CreateXstr((0x8000, [0xC3, 0x28]));
        var invalidUtf8 = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId,
            XexTitleFixtures.CreateXdbfTables((1, invalidUtf8Table)));

        await AssertMetadataWarningAsync(invalidUtf8);
    }

    [Fact]
    public async Task ReadAsync_TruncatedXstrTitleReturnsWarning()
    {
        var truncatedTable = XexTitleFixtures.CreateXstr((0x8000, Encoding.UTF8.GetBytes("short")));
        BinaryPrimitives.WriteUInt16BigEndian(truncatedTable.AsSpan(16), 40);
        var truncated = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId,
            XexTitleFixtures.CreateXdbfTables((1, truncatedTable)));

        await AssertMetadataWarningAsync(truncated);
    }

    [Fact]
    public async Task ReadAsync_TruncatedExecutableResourceReturnsWarning()
    {
        var executable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Truncated file")));
        Array.Resize(ref executable, executable.Length - 1);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("truncated-resource.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.Contains("outside", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ResourceLargerThanSixteenMiBReturnsWarning()
    {
        var executable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId,
            XexTitleFixtures.CreateXdbf((1, "Too large")),
            declaredResourceLength: 16 * 1024 * 1024 + 1);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("oversized.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.Contains("extent", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ResourceExactlySixteenMiBIsAccepted()
    {
        var xdbf = XexTitleFixtures.CreateXdbf((1, "Maximum resource title"));
        Array.Resize(ref xdbf, 16 * 1024 * 1024);
        var executable = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, xdbf);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("maximum-resource.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal("Maximum resource title", result.Title);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ReadAsync_EncryptedXexWithMissingSecurityReturnsWarning()
    {
        var xdbf = XexTitleFixtures.CreateXdbf((1, "Protected title"));
        var encrypted = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, xdbf, encryption: 1);

        var encryptedResult = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("encrypted.xex", encrypted, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(encryptedResult.Title);
        Assert.Contains("security", encryptedResult.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_UnsupportedCompressionReturnsWarning()
    {
        var unsupportedCompression = XexTitleFixtures.CreateExecutable(
            XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Protected title")),
            compression: 3);

        var compressionResult = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("compressed.xex", unsupportedCompression, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(compressionResult.Title);
        Assert.Contains("compression type 3", compressionResult.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, (ushort)0)]
    [InlineData(true, (ushort)0)]
    [InlineData(false, (ushort)1)]
    [InlineData(true, (ushort)1)]
    public async Task ReadAsync_EncryptedStoredImagesDecodeUtf8TitleWithoutChangingSource(bool devkit,
        ushort compression)
    {
        const string title = "Synthetic café 世界 😀";
        var executable = XexTitleFixtures.CreateProtectedExecutable(title, devkit, compression);
        var original = executable.ToArray();
        var file = new MemoryVirtualFile("not-the-display-title.xex", executable, DateTimeOffset.UnixEpoch);

        var result = await XexTitleReader.ReadAsync(file, XexTitleFixtures.DefaultTitleId);

        Assert.Equal(title, result.Title);
        Assert.Null(result.Warning);
        Assert.Contains(executable.AsSpan(XexTitleFixtures.ProtectedImageOffset + 16, 16).ToArray(),
            value => value != 0); // Nonzero previous ciphertext is required as the random-read IV.
        Assert.Equal(original, executable);
        var exported = new byte[executable.Length];
        await file.ReadExactlyAsync(0, exported);
        Assert.Equal(original, exported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_EncryptedLzxLinkedBlocksDecodeUtf8TitleWithoutChangingSource(bool devkit)
    {
        const string title = "Local LZX café 世界 😀";
        var executable = XexTitleFixtures.CreateProtectedExecutable(title, devkit, compression: 2);
        var original = executable.ToArray();
        var file = new MemoryVirtualFile("wrong-filename.xex", executable, DateTimeOffset.UnixEpoch);

        var result = await XexTitleReader.ReadAsync(file, XexTitleFixtures.DefaultTitleId);

        Assert.Equal(title, result.Title);
        Assert.Null(result.Warning);
        var exported = new byte[executable.Length];
        await file.ReadExactlyAsync(0, exported);
        Assert.Equal(original, executable);
        Assert.Equal(original, exported);
    }

    [Theory]
    [InlineData(1, 15, false, false)]
    [InlineData(1, 16, true, true)]
    [InlineData(2, 21, false, true)]
    [InlineData(2, 16, true, false)]
    [InlineData(3, 21, false, true)]
    public async Task ReadAsync_LzxFramesDecodeCrossFrameTitleWithoutChangingSource(int blockType,
        int windowBits, bool encrypted, bool translate)
    {
        const string title = "Cross-frame title";
        byte[] executable = XexTitleFixtures.CreateLzxExecutable(title, blockType, windowBits,
            translate: translate, encrypted: encrypted);
        byte[] original = executable.ToArray();

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("framed.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal(title, result.Title);
        Assert.Null(result.Warning);
        Assert.Equal(original, executable);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(32751, false)]
    [InlineData(32768, false)]
    [InlineData(32738, true)]
    [InlineData(32739, true)]
    [InlineData(32740, true)]
    public async Task ReadAsync_LzxResourceStartsAtOrAcrossFrameBoundary(int resourceStart, bool translate)
    {
        const string title = "Frame boundary title";
        byte[] executable = XexTitleFixtures.CreateLzxExecutable(title, 1, 16, resourceStart, translate);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("boundary.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal(title, result.Title);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadAsync_LzxShortFinalFrameDecodesCompleteResource(int blockType)
    {
        const string title = "Short final frame";
        byte[] executable = XexTitleFixtures.CreateLzxExecutable(title, blockType, 21,
            translate: true, trailingBytes: 0);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("final-frame.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal(title, result.Title);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("invalid-lzx")]
    [InlineData("truncated-lzx")]
    public async Task ReadAsync_MalformedLzxCodecDataReturnsWarning(string fault)
    {
        byte[] executable = XexTitleFixtures.CreateLzxExecutable("Malformed", 1, 16, fault: fault);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("malformed.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.Contains("LZX", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_LzxStopsAfterResourceFrameWithLargeDeclaredImage()
    {
        const string title = "Bounded resource";
        byte[] executable = XexTitleFixtures.CreateLzxExecutable(title, 1, 15,
            resourceStart: 37, fault: "large-image");

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("large-image.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Equal(title, result.Title);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ReadAsync_LzxCancellationAfterBlockReadPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var file = new CancelAfterImageReadFile(
            XexTitleFixtures.CreateLzxExecutable("Cancelled", 1, 15), cancellation);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            XexTitleReader.ReadAsync(file, XexTitleFixtures.DefaultTitleId, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(file.ImageReadCompleted);
    }

    [Theory]
    [InlineData("non-power-window", "window")]
    [InlineData("small-window", "window")]
    [InlineData("large-window", "window")]
    [InlineData("bad-sha", "checksum")]
    [InlineData("missing-terminator", "terminator")]
    [InlineData("truncated-chunk", "chunk")]
    [InlineData("invalid-extent", "outside")]
    [InlineData("expanded-limit", "outside")]
    [InlineData("resource-limit", "512 MiB")]
    public async Task ReadAsync_InvalidLzxFramingReturnsWarning(string fault, string warningFragment)
    {
        var executable = XexTitleFixtures.CreateProtectedExecutable("Invalid LZX title",
            devkit: true, compression: 2, fault: fault);

        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("invalid-lzx.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.Contains(warningFragment, result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_SparseLzxChainExceeding128MiBReturnsWarning()
    {
        var result = await XexTitleReader.ReadAsync(XexTitleFixtures.CreateOversizedLzxChain(),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.Contains("block chain", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    public async Task ReadAsync_EncryptedImageReadCancellationPropagates(ushort compression)
    {
        using var cancellation = new CancellationTokenSource();
        var file = new ImageReadFailureFile(
            XexTitleFixtures.CreateProtectedExecutable("Cancelled", compression: compression),
            () =>
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            XexTitleReader.ReadAsync(file, XexTitleFixtures.DefaultTitleId, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(file.ImageReadAttempted);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    public async Task ReadAsync_EncryptedImageReadIoFailurePropagates(ushort compression)
    {
        var error = new IOException("Synthetic ciphertext I/O failure");
        var file = new ImageReadFailureFile(
            XexTitleFixtures.CreateProtectedExecutable("Unreadable", compression: compression), () => error);

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            XexTitleReader.ReadAsync(file, XexTitleFixtures.DefaultTitleId));

        Assert.Same(error, actual);
        Assert.True(file.ImageReadAttempted);
    }

    [Fact]
    public async Task ReadAsync_CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executable = new MemoryVirtualFile(
            "cancelled.xex", XexTitleFixtures.CreateExecutable(
                XexTitleFixtures.DefaultTitleId, XexTitleFixtures.CreateXdbf((1, "Title"))),
            DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            XexTitleReader.ReadAsync(executable, XexTitleFixtures.DefaultTitleId, cancellation.Token));
    }

    [Fact]
    public async Task ReadAsync_IoFailurePropagates()
    {
        var error = new IOException("synthetic read failure");

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            XexTitleReader.ReadAsync(XexTitleFixtures.CreateFailingFile(error), XexTitleFixtures.DefaultTitleId));

        Assert.Same(error, thrown);
    }

    private static async Task AssertMetadataWarningAsync(byte[] executable)
    {
        var result = await XexTitleReader.ReadAsync(
            new MemoryVirtualFile("invalid-metadata.xex", executable, DateTimeOffset.UnixEpoch),
            XexTitleFixtures.DefaultTitleId);

        Assert.Null(result.Title);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
    }

    private sealed class ImageReadFailureFile(byte[] bytes, Func<Exception> failure)
        : VirtualFile("image-read-failure.xex", DateTimeOffset.UnixEpoch)
    {
        private readonly MemoryVirtualFile source = new("source.xex", bytes, DateTimeOffset.UnixEpoch);
        public bool ImageReadAttempted { get; private set; }
        public override long Length => source.Length;

        public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (offset >= XexTitleFixtures.ProtectedImageOffset)
            {
                ImageReadAttempted = true;
                return ValueTask.FromException<int>(failure());
            }
            return source.ReadAsync(offset, buffer, cancellationToken);
        }
    }

    private sealed class CancelAfterImageReadFile(byte[] bytes, CancellationTokenSource cancellation)
        : VirtualFile("cancel-after-read.xex", DateTimeOffset.UnixEpoch)
    {
        private readonly MemoryVirtualFile source = new("source.xex", bytes, DateTimeOffset.UnixEpoch);
        public bool ImageReadCompleted { get; private set; }
        public override long Length => source.Length;

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(offset, buffer, cancellationToken);
            if (offset >= XexTitleFixtures.ProtectedImageOffset)
            {
                ImageReadCompleted = true;
                cancellation.Cancel();
            }
            return read;
        }
    }
}
