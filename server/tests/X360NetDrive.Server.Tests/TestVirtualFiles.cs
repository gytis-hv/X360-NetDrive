using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

internal class SparseVirtualFile(string name, long length) : VirtualFile(name, FixedTime)
{
    private static readonly DateTimeOffset FixedTime =
        new(2024, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private readonly List<(long Offset, byte[] Bytes)> segments = [];

    public override long Length { get; } = length;

    public void Write(long offset, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length || bytes.Length > Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        segments.Add((offset, bytes.ToArray()));
    }

    public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
            return ValueTask.FromResult(0);

        var count = (int)Math.Min(buffer.Length, Length - offset);
        var destination = buffer[..count];
        destination.Span.Clear();
        var end = offset + count;
        foreach (var segment in segments)
        {
            var start = Math.Max(offset, segment.Offset);
            var segmentEnd = segment.Offset + segment.Bytes.Length;
            var copyEnd = Math.Min(end, segmentEnd);
            if (start < copyEnd)
                segment.Bytes.AsMemory((int)(start - segment.Offset), (int)(copyEnd - start))
                    .CopyTo(destination[(int)(start - offset)..]);
        }
        return ValueTask.FromResult(count);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"x360-netdrive-tests-{Guid.NewGuid():N}");

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
