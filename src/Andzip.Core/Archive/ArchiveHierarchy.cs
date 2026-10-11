namespace Andzip.Core.Archive;

/// <summary>
/// 压缩包内的一个目录节点：左侧树的骨架，也是右侧列表「当前目录」的数据源。
/// <para>
/// 为什么需要它：清单是**扁平**的（<see cref="ArchiveListing.Entries"/> 按写入顺序给出
/// <c>a/b/c.txt</c> 这样的路径），而界面要的是「选中哪个目录 → 显示它的直接子项」。
/// 更麻烦的是很多包根本不写目录条目（ZIP 允许只存文件路径），所以目录树必须能从文件路径
/// 反推出来，而不是照抄清单里 is_dir 为真的那些行。
/// </para>
/// <para>
/// 路径恒用 <c>/</c> 分隔且不带结尾斜杠；压缩包根节点的 <see cref="Path"/> 是空串。
/// </para>
/// </summary>
public sealed class ArchiveFolderNode
{
    private readonly List<ArchiveFolderNode> _folders = [];
    private readonly List<ArchiveEntryInfo> _files = [];
    private IReadOnlyList<ArchiveEntryInfo>? _rows;

    internal ArchiveFolderNode(string path)
    {
        Path = path;
        Entry = new ArchiveEntryInfo(path + (path.Length > 0 ? "/" : string.Empty), NameOf(path), true, 0, 0, false, null, null);
    }

    /// <summary>目录路径，不带结尾斜杠；根为 <c>""</c>。</summary>
    public string Path { get; }

    /// <summary>路径最后一段；根为空串（界面在根节点显示压缩包文件名）。</summary>
    public string Name => Entry.Name;

    /// <summary>本目录在父节点列表里对应的那一行。清单里有真实目录条目时就是它，否则是合成的。</summary>
    public ArchiveEntryInfo Entry { get; private set; }

    public ArchiveFolderNode? Parent { get; internal set; }

    /// <summary>子目录，按名称排序。</summary>
    public IReadOnlyList<ArchiveFolderNode> Folders => _folders;

    /// <summary>直接子项：目录在前、文件在后，各自按名称排序（与资源管理器 / 参考软件一致）。</summary>
    public IReadOnlyList<ArchiveEntryInfo> Rows => _rows ??= BuildRows();

    /// <summary>是否压缩包根（没有上级，所以列表里不出现 <c>..</c>）。</summary>
    public bool IsRoot => Path.Length == 0;

    internal void AddFolder(ArchiveFolderNode node) => _folders.Add(node);

    internal void AddFile(ArchiveEntryInfo entry) => _files.Add(entry);

    /// <summary>清单里读到同路径的目录条目时，用它的真实元数据（时间 / CRC / 属性）替换合成行。</summary>
    internal void AdoptEntry(ArchiveEntryInfo entry) => Entry = entry;

    internal void Seal()
    {
        _folders.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _rows = BuildRows();
        foreach (var folder in _folders)
        {
            folder.Seal();
        }
    }

    private IReadOnlyList<ArchiveEntryInfo> BuildRows()
    {
        var rows = _folders.ConvertAll(f => f.Entry);
        rows.AddRange(_files.OrderBy(e => e.Name, StringComparer.Ordinal));
        return rows;
    }

    private static string NameOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }
}

/// <summary>
/// 扁平清单 → 目录树（FR-4 压缩包视图）。
/// </summary>
public static class ArchiveHierarchy
{
    /// <summary>
    /// 建树。目录条目可能缺失、可能带尾斜杠、也可能出现 <c>a//b</c> 这类退化写法，
    /// 所以路径先按 <c>/</c> 切段并丢掉空段，再据此定位父节点 —— 保证同一目录不会因为
    /// 写法不同而在树上出现两个节点。
    /// </summary>
    public static ArchiveFolderNode Build(ArchiveListing listing)
        => Build(listing.Entries);

    public static ArchiveFolderNode Build(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var root = new ArchiveFolderNode(string.Empty);
        var nodes = new Dictionary<string, ArchiveFolderNode>(StringComparer.Ordinal) { [string.Empty] = root };

        foreach (var entry in entries)
        {
            var segments = SplitSegments(entry.Path);
            if (segments.Count == 0)
            {
                // "." / "" / "/" 之类的根占位条目：它描述的就是根节点本身，跳过。
                continue;
            }

            var ownerPath = string.Join('/', segments);
            var parentPath = string.Join('/', segments[..^1]);

            if (entry.IsDirectory)
            {
                var node = EnsureNode(nodes, root, ownerPath);
                node.AdoptEntry(entry);
                continue;
            }

            EnsureNode(nodes, root, parentPath).AddFile(entry);
        }

        root.Seal();
        return root;
    }

    /// <summary>条目路径的目录部分（<c>/</c> 分隔、不带尾斜杠）。根下的文件返回空串。</summary>
    public static string ParentPathOf(string entryPath)
    {
        var segments = SplitSegments(entryPath);
        return segments.Count <= 1 ? string.Empty : string.Join('/', segments[..^1]);
    }

    private static ArchiveFolderNode EnsureNode(
        Dictionary<string, ArchiveFolderNode> nodes,
        ArchiveFolderNode root,
        string path)
    {
        if (nodes.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var segments = SplitSegments(path);
        var current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var nodePath = string.Join('/', segments[..(i + 1)]);
            if (nodes.TryGetValue(nodePath, out var found))
            {
                current = found;
                continue;
            }

            var node = new ArchiveFolderNode(nodePath) { Parent = current };
            current.AddFolder(node);
            nodes[nodePath] = node;
            current = node;
        }

        return current;
    }

    private static List<string> SplitSegments(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
}
