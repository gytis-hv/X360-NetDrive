using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using X360NetDrive.Server.Fat;
using X360NetDrive.Server.God;
using X360NetDrive.Server.Iso;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Library;

public sealed record LibraryGame(string Source, string Title, string TitleId, string MediaId,
    byte DiscNumber, byte DiscCount, string LaunchPath, string? GodPackage);

public sealed record ExcludedIso(string Source, string Reason);
public sealed record LibraryWarning(string Source, string Reason);

public sealed class IsoLibrary : IDisposable
{
    private readonly List<IDisposable> resources;
    public VirtualDirectory Root { get; }
    public IReadOnlyList<LibraryGame> Games { get; }
    public IReadOnlyList<ExcludedIso> Excluded { get; }
    public IReadOnlyList<LibraryWarning> Warnings { get; }
    public string Snapshot { get; }
    public bool IsSingleIso { get; }

    private IsoLibrary(VirtualDirectory root, LibraryGame[] games, ExcludedIso[] excluded,
        LibraryWarning[] warnings, string snapshot, bool isSingleIso, List<IDisposable> resources)
    {
        Root = root;
        Games = games;
        Excluded = excluded;
        Warnings = warnings;
        Snapshot = snapshot;
        IsSingleIso = isSingleIso;
        this.resources = resources;
    }

    public static async Task<IsoLibrary> OpenAsync(string? isoPath, string? isoRoot,
        string? titleName, bool includeGod, string cacheDirectory, ILogger logger,
        CancellationToken cancellationToken = default, bool isoMetadataLayout = true)
    {
        bool single = !string.IsNullOrWhiteSpace(isoPath);
        bool recursive = !string.IsNullOrWhiteSpace(isoRoot);
        if (single == recursive)
            throw new ArgumentException("Supply exactly one of --iso <file> or --iso-root <directory>.");
        if (recursive && titleName is not null)
            throw new ArgumentException("--title is only supported with --iso, not --iso-root.");

        string? selectedPath = recursive ? isoRoot : isoPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        string source = Path.GetFullPath(selectedPath);
        string[] paths;
        if (recursive || Directory.Exists(source))
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"ISO root does not exist: {source}");
            paths = Directory.EnumerateFiles(source, "*.iso", new EnumerationOptions
            {
                RecurseSubdirectories = recursive, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive
            }).Order(StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).ToArray();
            if (paths.Length == 0)
                throw new InvalidDataException("No ISO files were found in the selected directory.");
            if (single && paths.Length != 1)
                throw new ArgumentException("The --iso directory must contain exactly one ISO; use --iso-root for a library.");
        }
        else
            paths = [source];

        var resources = new List<IDisposable>();
        bool published = false;
        try
        {
            var games = new List<LibraryGame>();
            var excluded = new List<ExcludedIso>();
            var warnings = new List<LibraryWarning>();
            var virtualPaths = new List<string>();
            var identities = new List<LibraryIdentity>();
            var folderGroups = new Dictionary<string, (int FirstIndex, int Count)>(StringComparer.OrdinalIgnoreCase);
            var tree = new VirtualDirectory("Games", DateTimeOffset.UnixEpoch);
            VirtualDirectory? content = null;
            DateTimeOffset lastModified = DateTimeOffset.MinValue;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = (recursive ? Path.GetRelativePath(source, path) : Path.GetFileName(path))
                    .Replace(Path.DirectorySeparatorChar, '/');
                LocalVirtualFile? file = null;
                GodPackage? god = null;
                try
                {
                    logger.LogInformation("Indexing ISO {Source}", relative);
                    file = new LocalVirtualFile(path);
                    var iso = await IsoImage.OpenAsync(file, cancellationToken, readTitle: isoMetadataLayout);
                    string title = titleName ?? (isoMetadataLayout ? iso.DisplayTitle : null)
                        ?? Path.GetFileNameWithoutExtension(path);
                    if (single && (!isoMetadataLayout || titleName is not null) && title.IndexOfAny(['/', '\\']) >= 0)
                        throw new ArgumentException("The --title value must be a single directory name.");
                    if (isoMetadataLayout && titleName is null && iso.DisplayTitle is null)
                        ReportWarning(relative, $"Using ISO filename for display title: {iso.TitleWarning}");
                    string virtualPath;
                    if (isoMetadataLayout)
                    {
                        string safeTitle = MakeTitleComponent(title);
                        if (safeTitle != title)
                            ReportWarning(relative, $"Display title normalized for FAT folder name: {safeTitle}");
                        string metadataSuffix = iso.TitleId == 0 ? string.Empty : $"[{iso.TitleId:X8}]";
                        if (iso.DiscNumber != 0 && iso.DiscCount != 0 &&
                            (iso.DiscNumber != 1 || iso.DiscCount != 1))
                            metadataSuffix += $"[Disc {iso.DiscNumber} of {iso.DiscCount}]";
                        if (iso.MediaId != 0)
                            metadataSuffix += $"[{iso.MediaId:X8}]";
                        string folder = metadataSuffix.Length == 0 ? safeTitle : $"{safeTitle} {metadataSuffix}";
                        string basePath = $"ISOs/{folder}";
                        if (folderGroups.TryGetValue(basePath, out var group))
                        {
                            int sequence = group.Count + 1;
                            if (group.Count == 1)
                            {
                                string firstPath = $"ISOs/{folder} (1)";
                                (tree.Find("ISOs") as VirtualDirectory ?? throw new InvalidDataException(
                                    "Metadata folder parent is missing.")).RenameChild(folder, $"{folder} (1)");
                                virtualPaths[group.FirstIndex] = firstPath;
                                games[group.FirstIndex] = games[group.FirstIndex] with
                                {
                                    LaunchPath = $"Games/{firstPath}/default.xex"
                                };
                                identities[group.FirstIndex] = identities[group.FirstIndex] with
                                {
                                    VirtualPath = firstPath
                                };
                            }
                            virtualPath = basePath;
                            do
                            {
                                virtualPath = $"{basePath} ({sequence++})";
                            }
                            while (virtualPaths.Contains(virtualPath, StringComparer.OrdinalIgnoreCase));
                            folderGroups[basePath] = (group.FirstIndex, sequence - 1);
                            ReportWarning(relative, $"Duplicate ISO metadata folder Games/ISOs/{folder}; exposed as Games/{virtualPath}.");
                        }
                        else
                        {
                            folderGroups.Add(basePath, (games.Count, 1));
                            virtualPath = basePath;
                        }
                    }
                    else
                        virtualPath = recursive ? relative[..^4] : title;
                    if (virtualPaths.Any(existing =>
                        string.Equals(existing, virtualPath, StringComparison.OrdinalIgnoreCase) ||
                        existing.StartsWith(virtualPath + "/", StringComparison.OrdinalIgnoreCase) ||
                        virtualPath.StartsWith(existing + "/", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"ISO export paths overlap at Games/{virtualPath}.");
                    string[] segments = virtualPath.Split('/');
                    var candidate = new VirtualDirectory("", file.LastModified);
                    var leaf = candidate;
                    foreach (var segment in segments)
                    {
                        var next = new VirtualDirectory(segment, file.LastModified);
                        leaf.Add(next);
                        leaf = next;
                    }
                    foreach (var child in iso.Root.Children)
                        leaf.Add(child);
                    _ = new Fat32Disk(candidate);
                    if (includeGod)
                    {
                        god = await GodPackage.CreateAsync(iso.Partition, title, iso.TitleId, iso.MediaId,
                            cacheDirectory, version: iso.Version, baseVersion: iso.BaseVersion,
                            discNumber: iso.DiscNumber, discCount: iso.DiscCount,
                            platform: iso.Platform, executableType: iso.ExecutableType,
                            cancellationToken: cancellationToken);
                        if (content?.Find(god.PackageRelativePath) is not null ||
                            content?.Find(god.PackageRelativePath + ".data") is not null)
                        {
                            string reason = $"Duplicate GOD package path: Content/{god.PackageRelativePath}";
                            if (!isoMetadataLayout)
                                throw new InvalidDataException(reason);
                            ReportWarning(relative, $"{reason}; keeping the ISO view without an additional GOD package.");
                            god.Dispose();
                            god = null;
                        }
                    }
                    var parent = tree;
                    foreach (var segment in segments[..^1])
                    {
                        if (parent.Find(segment) is VirtualDirectory directory)
                            parent = directory;
                        else
                        {
                            var next = new VirtualDirectory(segment, file.LastModified);
                            parent.Add(next);
                            parent = next;
                        }
                    }
                    parent.Add(leaf);
                    if (god is not null)
                    {
                        if (content is null)
                            content = god.ContentRoot;
                        else
                            MergeContent(content, god.ContentRoot);
                    }
                    games.Add(new LibraryGame(relative, title, $"{iso.TitleId:X8}", $"{iso.MediaId:X8}",
                        iso.DiscNumber, iso.DiscCount, $"Games/{virtualPath}/default.xex",
                        god is null ? null : $"Content/{god.PackageRelativePath}"));
                    virtualPaths.Add(virtualPath);
                    identities.Add(new LibraryIdentity(file.SourcePath, file.Length, file.LastModified.UtcTicks, virtualPath));
                    if (file.LastModified > lastModified)
                        lastModified = file.LastModified;
                    resources.Add(file);
                    file = null;
                    if (god is not null)
                    {
                        resources.Add(god);
                        god = null;
                    }
                }
                catch (Exception error) when (recursive &&
                    (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException))
                {
                    logger.LogWarning("Excluded ISO {Source}: {Reason}", relative, error.Message);
                    excluded.Add(new ExcludedIso(relative, error.Message));
                }
                finally
                {
                    try { god?.Dispose(); }
                    finally { file?.Dispose(); }
                }
            }
            if (games.Count == 0)
                throw new InvalidDataException($"No supported ISO files could be exposed ({excluded.Count} excluded). See the startup warnings.");
            var root = new VirtualDirectory("", lastModified);
            var gameRoot = new VirtualDirectory("Games", lastModified);
            foreach (var child in tree.Children)
                gameRoot.Add(child);
            root.Add(gameRoot);
            if (content is not null)
                root.Add(content);
            string identity = JsonSerializer.Serialize(new { Layout = "library-v2", includeGod, isoMetadataLayout, Sources = identities });
            string snapshot = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant()[..24];
            var library = new IsoLibrary(root, games.ToArray(), excluded.ToArray(), warnings.ToArray(), snapshot, single, resources);
            published = true;
            return library;

            void ReportWarning(string relative, string reason)
            {
                logger.LogWarning("ISO {Source}: {Reason}", relative, reason);
                warnings.Add(new LibraryWarning(relative, reason));
            }
        }
        finally
        {
            if (!published)
                DisposeResources(resources);
        }
    }

    private static string MakeTitleComponent(string title)
    {
        var result = new StringBuilder();
        foreach (var rune in title.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > 160)
                break;
            bool invalid = Rune.IsControl(rune) || rune.Value is 0xFFFE or 0xFFFF ||
                (rune.IsBmp && "\"*/:<>?\\|".Contains((char)rune.Value));
            result.Append(invalid ? "_" : rune.ToString());
        }
        string value = result.ToString().Trim().TrimEnd(' ', '.');
        return value.Length == 0 ? "Untitled" : value;
    }

    private static void MergeContent(VirtualDirectory destination, VirtualDirectory source)
    {
        foreach (var child in source.Children)
        {
            if (child is VirtualDirectory directory && destination.Find(child.Name) is VirtualDirectory existing)
                MergeContent(existing, directory);
            else
                destination.Add(child);
        }

    }

    private static void DisposeResources(List<IDisposable> resources)
    {
        for (int index = resources.Count - 1; index >= 0; index--)
            resources[index].Dispose();
        resources.Clear();
    }

    public void Dispose() => DisposeResources(resources);

    private sealed record LibraryIdentity(string SourcePath, long Length, long Modified, string VirtualPath);
}
