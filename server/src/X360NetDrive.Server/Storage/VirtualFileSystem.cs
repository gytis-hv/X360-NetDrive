using Microsoft.Win32.SafeHandles;

namespace X360NetDrive.Server.Storage;

public abstract class VirtualNode(string name, DateTimeOffset lastModified)
{
    public string Name { get; private set; } = name;
    public DateTimeOffset LastModified { get; } = lastModified;

    internal void Rename(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Invalid virtual file name.", nameof(name));
        Name = name;
    }
}

public sealed class VirtualDirectory(string name, DateTimeOffset lastModified)
    : VirtualNode(name, lastModified)
{
    private readonly List<VirtualNode> children = [];
    public IReadOnlyList<VirtualNode> Children => children;

    internal void RenameChild(string name, string newName)
    {
        var node = children.Find(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Virtual directory does not contain {name}.");
        if (children.Any(child => !ReferenceEquals(child, node) &&
            string.Equals(child.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Duplicate virtual file name: {newName}");
        node.Rename(newName);
    }

    public void Add(VirtualNode node)
    {
        if (string.IsNullOrEmpty(node.Name) || node.Name is "." or ".." ||
            node.Name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Invalid virtual file name.", nameof(node));
        if (children.Any(child => string.Equals(child.Name, node.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Duplicate virtual file name: {node.Name}");
        children.Add(node);
    }

    public VirtualNode? Find(string path)
    {
        VirtualNode current = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.Contains('\\') || part.Contains('\0'))
                return null;
            if (current is not VirtualDirectory directory)
                return null;
            var next = directory.children.Find(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            current = next;
        }
        return current;
    }
}

public abstract class VirtualFile(string name, DateTimeOffset lastModified)
    : VirtualNode(name, lastModified)
{
    public abstract long Length { get; }
    public abstract ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    public async ValueTask ReadExactlyAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var read = await ReadAsync(offset, buffer, cancellationToken);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of virtual file {Name}.");
            offset += read;
            buffer = buffer[read..];
        }
    }
}

public sealed class LocalVirtualFile : VirtualFile, IDisposable
{
    private readonly SafeFileHandle handle;
    public override long Length { get; }
    public string SourcePath { get; }

    public LocalVirtualFile(string path)
        : base(Path.GetFileName(path), File.GetLastWriteTimeUtc(path))
    {
        SourcePath = Path.GetFullPath(path);
        handle = File.OpenHandle(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(handle);
    }

    public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
            return ValueTask.FromResult(0);
        return RandomAccess.ReadAsync(handle, buffer[..(int)Math.Min(buffer.Length, Length - offset)], offset, cancellationToken);
    }

    public void Dispose() => handle.Dispose();
}

public sealed class SliceVirtualFile : VirtualFile
{
    private readonly VirtualFile source;
    private readonly long start;
    public override long Length { get; }

    public SliceVirtualFile(string name, VirtualFile source, long start, long length)
        : base(name, source.LastModified)
    {
        if (start < 0 || length < 0 || start > source.Length || length > source.Length - start)
            throw new ArgumentOutOfRangeException(nameof(start), "Slice lies outside the source file.");
        this.source = source;
        this.start = start;
        Length = length;
    }

    public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
            return ValueTask.FromResult(0);
        return source.ReadAsync(checked(start + offset), buffer[..(int)Math.Min(buffer.Length, Length - offset)], cancellationToken);
    }
}

public sealed class MemoryVirtualFile(string name, ReadOnlyMemory<byte> data, DateTimeOffset lastModified)
    : VirtualFile(name, lastModified)
{
    public override long Length => data.Length;

    public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var count = offset >= Length ? 0 : (int)Math.Min(buffer.Length, Length - offset);
        if (count > 0)
            data.Span.Slice((int)offset, count).CopyTo(buffer.Span);
        return ValueTask.FromResult(count);
    }
}
