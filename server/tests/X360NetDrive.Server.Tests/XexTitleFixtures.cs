using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

internal static class XexTitleFixtures
{
    internal const uint DefaultTitleId = 0xA1B2C3D4;
    internal const uint DefaultMediaId = 0x11223344;
    private const uint ImageBase = 0x82000000;
    private const uint ImageOffset = 0x200;
    private const uint ResourceHeaderOffset = 0x60;
    private const uint FormatHeaderOffset = 0x80;
    private const uint ExecutionInfoOffset = 0xA0;

    internal static byte[] CreateXdbf(params (ulong Language, string? Title)[] languages)
    {
        var tables = languages.Select(language =>
        {
            var entries = new List<(ushort Id, byte[] Value)>
            {
                (0x7000, Encoding.UTF8.GetBytes("ignored"))
            };
            if (language.Title is not null)
                entries.Add((0x8000, Encoding.UTF8.GetBytes(language.Title)));
            return (language.Language, CreateXstr(entries.ToArray()));
        }).ToArray();
        return CreateXdbfTables(tables);
    }

    internal static byte[] CreateXdbfTables(params (ulong Language, byte[] Table)[] tables)
    {
        int contentOffset = checked(24 + tables.Length * 18);
        int totalLength = checked(contentOffset + tables.Sum(table => table.Table.Length));
        var data = new byte[totalLength];
        "XDBF"u8.CopyTo(data);
        Write32(data, 4, 0x10000);
        Write32(data, 8, checked((uint)tables.Length));
        Write32(data, 12, checked((uint)tables.Length));
        int dataOffset = 0;
        for (int index = 0; index < tables.Length; index++)
        {
            int entryOffset = 24 + index * 18;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(entryOffset), 3);
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(entryOffset + 2), tables[index].Language);
            Write32(data, entryOffset + 10, checked((uint)dataOffset));
            Write32(data, entryOffset + 14, checked((uint)tables[index].Table.Length));
            tables[index].Table.CopyTo(data, contentOffset + dataOffset);
            dataOffset += tables[index].Table.Length;
        }
        return data;
    }

    internal static byte[] CreateXstr(params (ushort Id, byte[] Value)[] entries)
    {
        int totalLength = checked(14 + entries.Sum(entry => 4 + entry.Value.Length));
        var data = new byte[totalLength];
        "XSTR"u8.CopyTo(data);
        Write32(data, 4, 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), checked((ushort)entries.Length));
        int offset = 14;
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), entry.Id);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2), checked((ushort)entry.Value.Length));
            entry.Value.CopyTo(data, offset + 4);
            offset += 4 + entry.Value.Length;
        }
        return data;
    }

    internal static byte[] CreateExecutable(
        uint titleId,
        byte[]? titleResource,
        uint mediaId = DefaultMediaId,
        byte discNumber = 1,
        byte discCount = 1,
        uint? resourceTitleId = null,
        uint? declaredResourceLength = null,
        uint? declaredResourceTableSize = null,
        uint resourceAddressAdjustment = 0,
        ushort encryption = 0,
        ushort compression = 0,
        uint zeroFillBeforeResource = 5)
    {
        byte[] resource = titleResource ?? [];
        bool hasResource = titleResource is not null;
        bool basicCompression = compression == 1;
        const int prefixDataLength = 3;
        int imagePrefixLength = basicCompression ? prefixDataLength : 0;
        uint resourceVirtualOffset = checked((uint)(imagePrefixLength + (basicCompression ? zeroFillBeforeResource : 0)));
        int physicalImageLength = checked(imagePrefixLength + resource.Length);
        uint tableSize = declaredResourceTableSize ?? checked((uint)(4 + (hasResource ? 16 : 0)));
        int fileLength = checked((int)Math.Max(
            ImageOffset + Math.Max(physicalImageLength, 1),
            ResourceHeaderOffset + tableSize));
        var executable = new byte[fileLength];
        "XEX2"u8.CopyTo(executable);
        Write32(executable, 8, ImageOffset);
        Write32(executable, 20, 4);
        Write32(executable, 24, 0x2FF);
        Write32(executable, 28, ResourceHeaderOffset);
        Write32(executable, 32, 0x3FF);
        Write32(executable, 36, FormatHeaderOffset);
        Write32(executable, 40, 0x10201);
        Write32(executable, 44, ImageBase);
        Write32(executable, 48, 0x00040006);
        Write32(executable, 52, ExecutionInfoOffset);

        Write32(executable, checked((int)ExecutionInfoOffset), mediaId);
        Write32(executable, checked((int)ExecutionInfoOffset + 4), 0x55667788);
        Write32(executable, checked((int)ExecutionInfoOffset + 8), 0x99AABBCC);
        Write32(executable, checked((int)ExecutionInfoOffset + 12), titleId);
        executable[checked((int)ExecutionInfoOffset + 16)] = 0x57;
        executable[checked((int)ExecutionInfoOffset + 17)] = 0xA3;
        executable[checked((int)ExecutionInfoOffset + 18)] = discNumber;
        executable[checked((int)ExecutionInfoOffset + 19)] = discCount;

        Write32(executable, checked((int)ResourceHeaderOffset), tableSize);
        if (hasResource)
        {
            Encoding.ASCII.GetBytes((resourceTitleId ?? titleId).ToString("X8")).CopyTo(executable,
                checked((int)ResourceHeaderOffset + 4));
            Write32(executable, checked((int)ResourceHeaderOffset + 12),
                checked(ImageBase + resourceVirtualOffset + resourceAddressAdjustment));
            Write32(executable, checked((int)ResourceHeaderOffset + 16),
                declaredResourceLength ?? checked((uint)resource.Length));
        }

        Write32(executable, checked((int)FormatHeaderOffset), basicCompression ? 24u : 8u);
        BinaryPrimitives.WriteUInt16BigEndian(executable.AsSpan(checked((int)FormatHeaderOffset + 4)), encryption);
        BinaryPrimitives.WriteUInt16BigEndian(executable.AsSpan(checked((int)FormatHeaderOffset + 6)), compression);
        if (basicCompression)
        {
            Write32(executable, checked((int)FormatHeaderOffset + 8), prefixDataLength);
            Write32(executable, checked((int)FormatHeaderOffset + 12), zeroFillBeforeResource);
            Write32(executable, checked((int)FormatHeaderOffset + 16), checked((uint)resource.Length));
            Write32(executable, checked((int)FormatHeaderOffset + 20), 0);
            executable[ImageOffset] = 0xAA;
            executable[ImageOffset + 1] = 0xBB;
            executable[ImageOffset + 2] = 0xCC;
            resource.CopyTo(executable, checked((int)ImageOffset + prefixDataLength));
        }
        else if (resource.Length != 0)
            resource.CopyTo(executable, checked((int)ImageOffset));

        return executable;
    }

    internal static VirtualFile CreateFailingFile(Exception error) => new FailingVirtualFile(error);

    internal const int ProtectedImageOffset = 0x400;
    internal const int ProtectedSecurityOffset = 0x200;

    internal static byte[] CreateProtectedExecutable(string title, bool devkit = false,
        ushort compression = 0, string? fault = null)
    {
        const int resourceStart = 37; // Both resource and CBC read are deliberately unaligned.
        var resource = CreateXdbf((1, title));
        byte[] image;
        byte[] format;
        int expandedLength;
        if (compression == 1)
        {
            // An unused eight-byte XDBF free-table slot must be synthesized by basic mapping.
            var withFreeTable = new byte[resource.Length + 8];
            resource.AsSpan(0, 42).CopyTo(withFreeTable);
            resource.AsSpan(42).CopyTo(withFreeTable.AsSpan(50));
            Write32(withFreeTable, 16, 1);
            resource = withFreeTable;
            image = new byte[resourceStart + resource.Length - 8];
            FillPrefix(image, resourceStart);
            resource.AsSpan(0, 42).CopyTo(image.AsSpan(resourceStart));
            resource.AsSpan(50).CopyTo(image.AsSpan(resourceStart + 42));
            format = new byte[16];
            Write32(format, 0, resourceStart + 42);
            Write32(format, 4, 8);
            Write32(format, 8, checked((uint)(resource.Length - 50)));
            expandedLength = resourceStart + resource.Length;
        }
        else
        {
            image = new byte[resourceStart + resource.Length];
            FillPrefix(image, resourceStart);
            resource.CopyTo(image, resourceStart);
            expandedLength = image.Length;
            format = [];
        }

        if (compression == 2)
        {
            // No copyrighted input: one raw/uncompressed LZX block, split across two
            // independently SHA1-linked XEX blocks and multiple length-prefixed chunks.
            byte[] lzx = CreateRawLzx(image);
            int split = 23;
            byte[] second = FrameLzxBlock(lzx[split..], 0, new byte[20], fault);
            byte[] secondHash = SHA1.HashData(second);
            byte[] first = FrameLzxBlock(lzx[..split], checked((uint)second.Length), secondHash, null);
            format = new byte[28];
            Write32(format, 0, fault switch
            {
                "non-power-window" => 65537,
                "small-window" => 16384,
                "large-window" => 4194304,
                _ => 32768
            });
            Write32(format, 4, checked((uint)first.Length));
            SHA1.HashData(first).CopyTo(format, 8);
            if (fault == "bad-sha")
                second[^1] ^= 0x5A; // Keep the first block valid; break its link to block two.
            image = [.. first, .. second];
        }

        byte[] header = CreateProtectedHeader(resourceStart, resource.Length, expandedLength, compression, format);
        if (fault == "invalid-extent")
            Write32(header, ProtectedSecurityOffset + 4, resourceStart);
        if (fault == "expanded-limit")
            Write32(header, ProtectedSecurityOffset + 4, 512 * 1024 * 1024 + 1);
        if (fault == "resource-limit")
            Write32(header, 0x6C, ImageBase + 512 * 1024 * 1024);

        return ProtectImage(header, image, devkit);
    }

    internal static byte[] CreateLzxExecutable(string title, int blockType, int windowBits,
        int resourceStart = 65519, bool translate = false, bool encrypted = false,
        int trailingBytes = 32768, string? fault = null)
    {
        byte[] resource = CreateXdbf((1, title));
        if (translate)
        {
            // E8 in the low byte of free-table capacity precedes a zero entry count.
            // The translation must be undone for the XDBF header to remain valid.
            var expanded = new byte[resource.Length + 232 * 8];
            resource.AsSpan(0, 42).CopyTo(expanded);
            resource.AsSpan(42).CopyTo(expanded.AsSpan(42 + 232 * 8));
            Write32(expanded, 16, 232);
            resource = expanded;
        }
        int imageLength = fault == "large-image" ? 32768 : resourceStart + resource.Length + trailingBytes;
        byte[] image = Enumerable.Range(0, imageLength).Select(index => (byte)(index % 64)).ToArray();
        resource.CopyTo(image, resourceStart);
        byte[] lzx = LzxFixtures.Encode(image, blockType, windowBits, translate);
        if (fault == "invalid-lzx")
            lzx.AsSpan(0, 2).Clear();
        if (fault == "truncated-lzx")
            lzx = lzx[..1];
        byte[] chain = [];
        byte[] hash = new byte[20];
        int nextSize = 0;
        for (int end = lzx.Length; end > 0;)
        {
            int start = Math.Max(0, end - 32768);
            byte[] block = FrameLzxBlock(lzx[start..end], (uint)nextSize, hash, null);
            hash = SHA1.HashData(block);
            nextSize = block.Length;
            chain = [.. block, .. chain];
            end = start;
        }
        var format = new byte[28];
        Write32(format, 0, checked((uint)(1 << windowBits)));
        Write32(format, 4, (uint)nextSize);
        hash.CopyTo(format, 8);
        byte[] header = CreateProtectedHeader(resourceStart, resource.Length,
            fault == "large-image" ? 512 * 1024 * 1024 : image.Length, 2, format);
        if (encrypted)
            return ProtectImage(header, chain, devkit: false);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x84), 0);
        return [.. header, .. chain];
    }

    private static byte[] ProtectImage(byte[] header, byte[] image, bool devkit)
    {
        // A fixed, test-only session key. Neither user keys nor real game data are used.
        byte[] sessionKey = Enumerable.Range(1, 16).Select(value => (byte)(value * 7)).ToArray();
        using var aes = Aes.Create();
        aes.Key = sessionKey;
        Array.Resize(ref image, (image.Length + 15) & ~15);
        byte[] ciphertext = aes.EncryptCbc(image, new byte[16], PaddingMode.None);
        using var wrappingAes = Aes.Create();
        wrappingAes.Key = devkit ? new byte[16] : Convert.FromHexString("20B185A59D28FDC340583FBB0896BF91");
        wrappingAes.EncryptCbc(sessionKey, new byte[16], PaddingMode.None)
            .CopyTo(header, ProtectedSecurityOffset + 0x150);
        return [.. header, .. ciphertext];
    }

    internal static VirtualFile CreateOversizedLzxChain()
    {
        // Nine nominal 16 MiB blocks exceed 128 MiB. Only headers are stored.
        // Reuse a single hash buffer instead of materializing a 144 MiB fixture.
        const int blockSize = 16 * 1024 * 1024;
        const int blockCount = 9;
        var source = new SparseVirtualFile("sparse-chain.xex",
            ProtectedImageOffset + (long)blockSize * blockCount);
        var buffer = new byte[blockSize];
        byte[] nextHash = new byte[20];
        for (int index = blockCount - 1; index >= 0; index--)
        {
            Write32(buffer, 0, index == blockCount - 1 ? 0u : blockSize);
            nextHash.CopyTo(buffer, 4);
            source.Write(ProtectedImageOffset + (long)index * blockSize, buffer.AsSpan(0, 26));
            nextHash = SHA1.HashData(buffer);
        }
        var format = new byte[28];
        Write32(format, 0, 32768);
        Write32(format, 4, blockSize);
        nextHash.CopyTo(format, 8);
        var header = CreateProtectedHeader(37, 100, 256, 2, format);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x84), 0);
        source.Write(0, header);
        return source;
    }

    private static byte[] CreateProtectedHeader(int resourceStart, int resourceLength,
        int expandedLength, ushort compression, byte[] format)
    {
        var header = new byte[ProtectedImageOffset];
        "XEX2"u8.CopyTo(header);
        Write32(header, 8, ProtectedImageOffset);
        Write32(header, 16, ProtectedSecurityOffset);
        Write32(header, 20, 4);
        Write32(header, 24, 0x2FF);
        Write32(header, 28, 0x60);
        Write32(header, 32, 0x3FF);
        Write32(header, 36, 0x80);
        Write32(header, 40, 0x10201);
        Write32(header, 44, ImageBase);
        Write32(header, 48, 0x40006);
        Write32(header, 52, 0xC0);
        Write32(header, 0x60, 20);
        Encoding.ASCII.GetBytes(DefaultTitleId.ToString("X8")).CopyTo(header, 0x64);
        Write32(header, 0x6C, checked(ImageBase + (uint)resourceStart));
        Write32(header, 0x70, checked((uint)resourceLength));
        Write32(header, 0x80, checked((uint)(8 + format.Length)));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x84), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x86), compression);
        format.CopyTo(header, 0x88);
        Write32(header, 0xC0, DefaultMediaId);
        Write32(header, 0xCC, DefaultTitleId);
        header[0xD0] = 0x57;
        header[0xD2] = 2;
        header[0xD3] = 3;
        Write32(header, ProtectedSecurityOffset + 4, checked((uint)expandedLength));
        return header;
    }

    private static void FillPrefix(byte[] image, int count)
    {
        for (int index = 0; index < count; index++)
            image[index] = (byte)(index * 3 + 1);
    }

    private static byte[] CreateRawLzx(byte[] image)
    {
        // LZX words are little endian, consumed most-significant bit first.
        // Intel flag 0 | block type 3 | 24-bit length | alignment | R0/R1/R2.
        uint bits = (3u << 28) | (checked((uint)image.Length) << 4);
        var lzx = new byte[16 + image.Length + (image.Length & 1)];
        BinaryPrimitives.WriteUInt16LittleEndian(lzx, (ushort)(bits >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(lzx.AsSpan(2), (ushort)bits);
        for (int index = 4; index < 16; index += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(lzx.AsSpan(index), 1);
        image.CopyTo(lzx, 16);
        return lzx;
    }

    private static byte[] FrameLzxBlock(byte[] chunk, uint nextSize, byte[] nextHash, string? fault)
    {
        int size = 24 + 2 + chunk.Length + (fault == "missing-terminator" ? 0 : 2);
        if (fault != "missing-terminator")
            size = (size + 15) & ~15;
        var block = new byte[size];
        Write32(block, 0, nextSize);
        nextHash.CopyTo(block, 4);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(24),
            fault == "truncated-chunk" ? ushort.MaxValue : checked((ushort)chunk.Length));
        chunk.CopyTo(block, 26);
        return block;
    }

    private static void Write32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);

    private sealed class FailingVirtualFile(Exception error) : VirtualFile("failing.xex", DateTimeOffset.UnixEpoch)
    {
        public override long Length => 256;

        public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(error);
    }
}
