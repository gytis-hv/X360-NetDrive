using System.Buffers.Binary;

namespace X360NetDrive.Server.Tests;

internal static class LzxFixtures
{
    internal static byte[] Encode(byte[] image, int blockType, int windowBits, bool translate)
    {
        byte[] data = image.ToArray();
        if (translate)
        {
            for (int frame = 0; frame < data.Length; frame += 32768)
            {
                int count = Math.Min(32768, data.Length - frame);
                for (int index = 0; index < count - 10; index++)
                {
                    if (data[frame + index] != 0xE8)
                        continue;
                    int position = frame + index;
                    int relative = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 1));
                    long target = (long)relative + position;
                    if (target >= 0 && target < (long)data.Length + position)
                        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(position + 1),
                            checked((int)(target < data.Length ? target : relative - (long)data.Length)));
                    index += 4;
                }
            }
        }

        var writer = new WordWriter();
        writer.Write(translate ? 1u : 0u, 1);
        if (translate)
        {
            writer.Write((uint)data.Length >> 16, 16);
            writer.Write((uint)data.Length & 0xFFFF, 16);
        }
        int slots = windowBits switch { 20 => 42, 21 => 50, _ => 2 * windowBits };
        const int repeatSymbol = 256 + 7;
        const int distanceSymbol = 256 + 12 * 8 + 7;
        var lengths = new byte[256 + slots * 8];
        Array.Fill(lengths, (byte)8, 0, 254);
        lengths[254] = lengths[255] = lengths[repeatSymbol] = lengths[distanceSymbol] = 9;
        uint[] codes = Codes(lengths);
        var lengthLengths = new byte[249];
        lengthLengths[55] = lengthLengths[56] = 1;
        var previousMain = new byte[lengths.Length];
        var previousLength = new byte[249];
        bool repeatedDistance = false;
        for (int position = 0; position < data.Length;)
        {
            int blockLength = Math.Min(65536, data.Length - position);
            writer.Write((uint)blockType, 3);
            writer.Write((uint)blockLength, 24);
            if (blockType == 3)
            {
                writer.Align(fullWordIfAligned: true);
                for (int index = 0; index < 3; index++)
                    writer.Append([1, 0, 0, 0]);
                writer.Append(data.AsSpan(position, blockLength));
                if (blockLength % 2 != 0)
                    writer.Append([0]);
                position += blockLength;
                continue;
            }
            if (blockType == 2)
                for (int index = 0; index < 8; index++)
                    writer.Write(3, 3);
            WriteLengths(writer, previousMain.AsSpan(0, 256), lengths.AsSpan(0, 256));
            WriteLengths(writer, previousMain.AsSpan(256), lengths.AsSpan(256));
            WriteLengths(writer, previousLength, lengthLengths);
            lengths.CopyTo(previousMain, 0);
            lengthLengths.CopyTo(previousLength, 0);
            int blockEnd = position + blockLength;
            while (position < blockEnd)
            {
                // Distance-64 matches exercise new/repeated offsets and aligned footer
                // bits. The title resource itself can contain arbitrary literal bytes.
                bool match = position >= 64 && blockEnd - position >= 64 &&
                    32768 - position % 32768 >= 64 &&
                    data.AsSpan(position, 64).SequenceEqual(data.AsSpan(position - 64, 64));
                if (match)
                {
                    int symbol = repeatedDistance ? repeatSymbol : distanceSymbol;
                    writer.Write(codes[symbol], lengths[symbol]);
                    writer.Write(0, 1);
                    if (!repeatedDistance)
                    {
                        if (blockType == 2)
                        {
                            writer.Write(0, 2);
                            writer.Write(2, 3);
                        }
                        else
                            writer.Write(2, 5);
                        repeatedDistance = true;
                    }
                    position += 64;
                }
                else
                {
                    byte literal = data[position++];
                    writer.Write(codes[literal], lengths[literal]);
                }
                if (position % 32768 == 0)
                    writer.Align();
            }
        }
        return writer.Finish();
    }

    private static void WriteLengths(WordWriter writer, ReadOnlySpan<byte> previous, ReadOnlySpan<byte> lengths)
    {
        byte[] preLengths = Enumerable.Range(0, 20).Select(index => (byte)(index < 12 ? 4 : 5)).ToArray();
        uint[] codes = Codes(preLengths);
        foreach (byte value in preLengths)
            writer.Write(value, 4);
        for (int index = 0; index < lengths.Length; index++)
        {
            int delta = (previous[index] - lengths[index] + 17) % 17;
            writer.Write(codes[delta], preLengths[delta]);
        }
    }

    private static uint[] Codes(byte[] lengths)
    {
        var counts = new uint[17];
        var next = new uint[17];
        foreach (byte length in lengths)
            if (length != 0)
                counts[length]++;
        uint code = 0;
        for (int length = 1; length <= 16; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }
        var result = new uint[lengths.Length];
        for (int index = 0; index < lengths.Length; index++)
            if (lengths[index] != 0)
                result[index] = next[lengths[index]]++;
        return result;
    }

    private sealed class WordWriter
    {
        private readonly List<byte> bytes = [];
        private uint word;
        private int bits;

        public void Write(uint value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                word = (word << 1) | ((value >> bit) & 1);
                if (++bits != 16)
                    continue;
                bytes.Add((byte)word);
                bytes.Add((byte)(word >> 8));
                word = 0;
                bits = 0;
            }
        }

        public void Align(bool fullWordIfAligned = false)
        {
            if (bits == 0 && fullWordIfAligned)
                Write(0, 16);
            while (bits != 0)
                Write(0, 1);
        }

        public void Append(ReadOnlySpan<byte> value) => bytes.AddRange(value.ToArray());

        public byte[] Finish()
        {
            Align();
            return bytes.ToArray();
        }
    }
}
