using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Iso;

public sealed record XexTitleMetadata(string? Title, string? Warning);

public static class XexTitleReader
{
    public static async Task<XexTitleMetadata> ReadAsync(VirtualFile executable, uint titleId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return new XexTitleMetadata(await ReadTitleAsync(executable, titleId, cancellationToken), null);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or DecoderFallbackException)
        {
            return new XexTitleMetadata(null, error.Message);
        }
    }

    private static async Task<string> ReadTitleAsync(VirtualFile executable, uint titleId,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[24];
        await executable.ReadExactlyAsync(0, prefix, cancellationToken);
        if (!prefix.AsSpan(0, 4).SequenceEqual("XEX2"u8))
            throw new InvalidDataException("Title metadata requires a XEX2 executable.");
        uint count = Read32(prefix, 20);
        if (count > 4096 || 24L + count * 8L > executable.Length)
            throw new InvalidDataException("Invalid XEX title optional-header table.");
        var headers = new byte[(int)count * 8];
        await executable.ReadExactlyAsync(24, headers, cancellationToken);
        var offsets = new Dictionary<uint, uint>();
        for (int at = 0; at < headers.Length; at += 8)
            if (!offsets.TryAdd(Read32(headers, at), Read32(headers, at + 4)))
                throw new InvalidDataException("Duplicate XEX optional-header key.");
        if (!offsets.TryGetValue(0x2FF, out uint resourceOffset) ||
            !offsets.TryGetValue(0x3FF, out uint formatOffset) ||
            !offsets.TryGetValue(0x10201, out uint imageBase))
            throw new InvalidDataException("XEX display-title resource information is absent.");
        var formatPrefix = new byte[8];
        await executable.ReadExactlyAsync(formatOffset, formatPrefix, cancellationToken);
        uint formatSize = Read32(formatPrefix, 0);
        ushort encryption = BinaryPrimitives.ReadUInt16BigEndian(formatPrefix.AsSpan(4));
        ushort compression = BinaryPrimitives.ReadUInt16BigEndian(formatPrefix.AsSpan(6));
        if (encryption is not (0 or 1))
            throw new InvalidDataException($"XEX title encryption type {encryption} is not supported.");
        if (compression is not (0 or 1 or 2))
            throw new InvalidDataException($"XEX display-title compression type {compression} is not supported.");
        if (formatSize < 8 || formatSize > 65536 || formatOffset > executable.Length - formatSize ||
            (compression == 1 && (formatSize - 8) % 8 != 0))
            throw new InvalidDataException("Invalid XEX display-title image format.");
        var resourcePrefix = new byte[4];
        await executable.ReadExactlyAsync(resourceOffset, resourcePrefix, cancellationToken);
        uint resourceSize = Read32(resourcePrefix, 0);
        if (resourceSize < 4 || resourceSize > 65536 || (resourceSize - 4) % 16 != 0 ||
            resourceOffset > executable.Length - resourceSize)
            throw new InvalidDataException("Invalid XEX title resource table.");
        var resources = new byte[(int)resourceSize - 4];
        await executable.ReadExactlyAsync(resourceOffset + 4L, resources, cancellationToken);
        string resourceName = titleId.ToString("X8", CultureInfo.InvariantCulture);
        for (int at = 0; at < resources.Length; at += 16)
        {
            if (!string.Equals(Encoding.ASCII.GetString(resources, at, 8), resourceName,
                StringComparison.OrdinalIgnoreCase))
                continue;
            uint address = Read32(resources, at + 8);
            uint length = Read32(resources, at + 12);
            if (address < imageBase || length is < 24 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid XEX display-title resource extent.");
            long imageOffset = Read32(prefix, 8);
            long start = (long)address - imageBase;
            byte[] data = await XexImageReader.ReadResourceAsync(executable, imageOffset, Read32(prefix, 16),
                formatOffset, formatSize, encryption, compression, start, (int)length, cancellationToken);
            return ReadXdbfTitle(data);
        }
        throw new InvalidDataException($"XEX title resource {resourceName} is absent.");
    }

    private static string ReadXdbfTitle(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || !data[..4].SequenceEqual("XDBF"u8) || Read32(data, 4) != 0x10000)
            throw new InvalidDataException("Invalid XDBF display-title metadata.");
        uint capacity = Read32(data, 8);
        uint count = Read32(data, 12);
        uint freeCapacity = Read32(data, 16);
        if (count > capacity || Read32(data, 20) > freeCapacity)
            throw new InvalidDataException("Invalid XDBF entry counts.");
        long contentOffset = 24L + capacity * 18L + freeCapacity * 8L;
        if (contentOffset > data.Length)
            throw new InvalidDataException("XDBF tables lie outside the title resource.");
        var tables = new List<(ulong Language, int Offset, int Length)>();
        for (int index = 0; index < count; index++)
        {
            int at = 24 + index * 18;
            if (BinaryPrimitives.ReadUInt16BigEndian(data[at..]) != 3)
                continue;
            ulong language = BinaryPrimitives.ReadUInt64BigEndian(data[(at + 2)..]);
            long offset = contentOffset + Read32(data, at + 10);
            uint length = Read32(data, at + 14);
            if (offset > data.Length - length)
                throw new InvalidDataException("XDBF string table lies outside the title resource.");
            tables.Add((language, (int)offset, (int)length));
        }
        foreach (var table in tables.OrderBy(table => table.Language == 1 ? 0 : 1).ThenBy(table => table.Language))
        {
            var strings = data.Slice(table.Offset, table.Length);
            if (strings.Length < 14 || !strings[..4].SequenceEqual("XSTR"u8) || Read32(strings, 4) != 1)
                throw new InvalidDataException("Invalid XDBF title string table.");
            ushort stringCount = BinaryPrimitives.ReadUInt16BigEndian(strings[12..]);
            int at = 14;
            for (int index = 0; index < stringCount; index++)
            {
                if (at > strings.Length - 4)
                    throw new InvalidDataException("Truncated XDBF title string entry.");
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(strings[at..]);
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(strings[(at + 2)..]);
                at += 4;
                if (at > strings.Length - length)
                    throw new InvalidDataException("XDBF title string lies outside its table.");
                if (id == 0x8000)
                {
                    string title = new UTF8Encoding(false, true).GetString(strings.Slice(at, length)).TrimEnd('\0').Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }
                at += length;
            }
        }
        throw new InvalidDataException("No display title was found in the XDBF string tables.");
    }

    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
}
