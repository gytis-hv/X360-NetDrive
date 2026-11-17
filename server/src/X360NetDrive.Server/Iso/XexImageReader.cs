using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Lzx;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Iso;

internal static class XexImageReader
{
    // Public XEX format constants, not user credentials. Decoding never changes exported executable bytes.
    private static readonly byte[][] WrappingKeys =
    [
        Convert.FromHexString("20B185A59D28FDC340583FBB0896BF91"),
        new byte[16]
    ];

    public static async Task<byte[]> ReadResourceAsync(VirtualFile executable, long imageOffset,
        uint securityOffset, uint formatOffset, uint formatSize, ushort encryption, ushort compression,
        long start, int length, CancellationToken cancellationToken)
    {
        if (start + length > 512L * 1024 * 1024)
            throw new InvalidDataException("XEX title resource exceeds the 512 MiB image limit.");
        var format = new byte[(int)formatSize - 8];
        await executable.ReadExactlyAsync(formatOffset + 8L, format, cancellationToken);
        if (encryption == 0)
            return await DecodeAsync(null);

        var security = new byte[0x160];
        if (securityOffset < 24 || securityOffset > executable.Length - security.Length)
            throw new InvalidDataException("XEX title encryption security information lies outside the executable.");
        await executable.ReadExactlyAsync(securityOffset, security, cancellationToken);
        InvalidDataException? lastError = null;
        foreach (var wrappingKey in WrappingKeys)
        {
            using var wrappingAes = Aes.Create();
            wrappingAes.Key = wrappingKey;
            byte[] key = wrappingAes.DecryptCbc(security.AsSpan(0x150, 16), new byte[16], PaddingMode.None);
            try
            {
                var resource = await DecodeAsync(key);
                if (!resource.AsSpan(0, 4).SequenceEqual("XDBF"u8))
                    throw new InvalidDataException("Decoded XEX title resource is not XDBF metadata.");
                return resource;
            }
            catch (InvalidDataException error)
            {
                lastError = error;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        throw new InvalidDataException($"Unable to decode encrypted XEX display-title metadata: {lastError?.Message}", lastError);

        async Task<byte[]> DecodeAsync(byte[]? key)
        {
            var resource = new byte[length];
            if (compression == 0)
                await ReadStoredAsync(executable, imageOffset, start, resource, key, cancellationToken);
            else if (compression == 1)
            {
                long imagePosition = 0;
                long filePosition = 0;
                for (int block = 0; block < format.Length; block += 8)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint dataSize = Read32(format, block);
                    uint zeroSize = Read32(format, block + 4);
                    if (imageOffset + filePosition > executable.Length - dataSize)
                        throw new InvalidDataException("XEX basic-compression block lies outside the executable.");
                    long from = Math.Max(start, imagePosition);
                    long to = Math.Min(start + length, imagePosition + dataSize);
                    if (to > from)
                        await ReadStoredAsync(executable, imageOffset, filePosition + from - imagePosition,
                            resource.AsMemory((int)(from - start), (int)(to - from)), key, cancellationToken);
                    filePosition += dataSize;
                    imagePosition += (long)dataSize + zeroSize;
                }
                if (start + length > imagePosition)
                    throw new InvalidDataException("XEX display-title resource lies outside the expanded image.");
            }
            else
                await ReadLzxResourceAsync(executable, imageOffset, securityOffset, format, start, resource, key,
                    cancellationToken);
            return resource;
        }
    }

    private static async Task ReadStoredAsync(VirtualFile executable, long imageOffset, long offset,
        Memory<byte> destination, byte[]? key, CancellationToken cancellationToken)
    {
        if (key is null)
        {
            if (imageOffset + offset > executable.Length - destination.Length)
                throw new InvalidDataException("XEX display-title resource lies outside the executable.");
            await executable.ReadExactlyAsync(imageOffset + offset, destination, cancellationToken);
            return;
        }
        long aligned = offset & ~15L;
        int skip = (int)(offset - aligned);
        int count = (skip + destination.Length + 15) & ~15;
        if (imageOffset + aligned > executable.Length - count)
            throw new InvalidDataException("Encrypted XEX title data lies outside the executable.");
        var iv = new byte[16];
        if (aligned != 0)
            await executable.ReadExactlyAsync(imageOffset + aligned - 16, iv, cancellationToken);
        var encrypted = new byte[count];
        await executable.ReadExactlyAsync(imageOffset + aligned, encrypted, cancellationToken);
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] decoded = aes.DecryptCbc(encrypted, iv, PaddingMode.None);
        decoded.AsMemory(skip, destination.Length).CopyTo(destination);
    }

    private static async Task ReadLzxResourceAsync(VirtualFile executable, long imageOffset, uint securityOffset,
        byte[] format, long start, Memory<byte> destination, byte[]? key, CancellationToken cancellationToken)
    {
        if (format.Length != 28)
            throw new InvalidDataException("Invalid XEX LZX compression information.");
        uint windowSize = Read32(format, 0);
        if (!BitOperations.IsPow2(windowSize) || windowSize is < 32768 or > 2097152)
            throw new InvalidDataException("Invalid XEX LZX window size.");
        var imageSizeBytes = new byte[4];
        if (securityOffset < 24 || securityOffset + 4L > executable.Length - 4)
            throw new InvalidDataException("XEX LZX image size lies outside the executable.");
        await executable.ReadExactlyAsync(securityOffset + 4L, imageSizeBytes, cancellationToken);
        uint imageSize = Read32(imageSizeBytes, 0);
        if (imageSize > 512 * 1024 * 1024 || start + destination.Length > imageSize)
            throw new InvalidDataException("XEX title resource lies outside the declared LZX image.");
        uint blockSize = Read32(format, 4);
        byte[] expectedHash = format.AsSpan(8, 20).ToArray();
        long storedOffset = 0;
        using var compressed = new MemoryStream();
        while (blockSize != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blockSize is < 26 or > 16 * 1024 * 1024 || storedOffset + blockSize > 128L * 1024 * 1024)
                throw new InvalidDataException("Invalid or oversized XEX LZX block chain.");
            var block = new byte[(int)blockSize];
            await ReadStoredAsync(executable, imageOffset, storedOffset, block, key, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(block), expectedHash))
                throw new InvalidDataException("XEX LZX block checksum does not match.");
            uint nextSize = Read32(block, 0);
            expectedHash = block.AsSpan(4, 20).ToArray();
            int at = 24;
            bool terminated = false;
            while (at <= block.Length - 2)
            {
                ushort chunkSize = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at));
                at += 2;
                if (chunkSize == 0)
                {
                    terminated = true;
                    break;
                }
                if (at > block.Length - chunkSize)
                    throw new InvalidDataException("XEX LZX chunk lies outside its block.");
                compressed.Write(block, at, chunkSize);
                at += chunkSize;
            }
            if (!terminated)
                throw new InvalidDataException("XEX LZX block has no chunk terminator.");
            storedOffset += blockSize;
            blockSize = nextSize;
        }
        var decoder = new LzxDecoder(BitOperations.Log2(windowSize),
            new LzxDecompressionOptions { MaxOutputSize = imageSize });
        var frame = new byte[LzxDecoder.FrameSize];
        int consumed = 0;
        long resourceEnd = start + destination.Length;
        try
        {
            for (long position = 0; position < resourceEnd; position += frame.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(frame.Length, imageSize - position);
                // E8 translation needs the complete frame, even when the title ends
                // partway through it. Retain only the requested resource bytes.
                decoder.DecodeFrame(compressed.GetBuffer().AsSpan(consumed, (int)compressed.Length - consumed),
                    frame.AsSpan(0, count), out int used);
                consumed += used;
                cancellationToken.ThrowIfCancellationRequested();
                long from = Math.Max(start, position);
                long to = Math.Min(resourceEnd, position + count);
                if (to > from)
                    frame.AsMemory((int)(from - position), (int)(to - from))
                        .CopyTo(destination[(int)(from - start)..]);
            }
        }
        catch (CompressionFormatException error)
        {
            throw new InvalidDataException("Malformed XEX LZX compressed data.", error);
        }
        catch (CompressionLimitException error)
        {
            throw new InvalidDataException("XEX LZX metadata decompression limit exceeded.", error);
        }
    }

    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
}
