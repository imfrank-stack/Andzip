using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 扁平清单 → 目录树（FR-4 压缩包视图左侧树 + 右侧「当前目录」列表的数据源）。
/// 这里只喂内存里的条目，不碰引擎：要验的是路径归并与排序规则，不是 7-Zip 的目录解析。
/// </summary>
public class ArchiveHierarchyTests
{
    private static ArchiveEntryInfo File(string path, long size = 10, int index = 0)
        => new(path, Name(path), false, size, size, false, "Deflate", null, index);

    private static ArchiveEntryInfo Dir(string path)
        => new(path + "/", Name(path), true, 0, 0, false, null, null);

    private static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static ArchiveListing Listing(params ArchiveEntryInfo[] entries)
        => new("test.zip", entries, entries.Count(e => !e.IsDirectory), entries.Count(e => e.IsDirectory), 0, 0, null);

    [Fact]
    public void Build_WithoutDirectoryEntries_RecoversTreeFromFilePaths()
    {
        // ZIP 允许只写文件路径、不写目录条目，树必须仍然完整。
        var root = ArchiveHierarchy.Build(Listing(
            File("a/b/c.txt"),
            File("a/d.txt"),
            File("top.txt")));

        Assert.Equal(string.Empty, root.Path);
        Assert.True(root.IsRoot);

        var a = Assert.Single(root.Folders);
        Assert.Equal("a", a.Path);
        Assert.Equal("a", a.Name);
        Assert.Same(root, a.Parent);

        Assert.Equal(["b", "d.txt"], a.Rows.Select(e => e.Name).ToArray());
        Assert.Equal(["a", "top.txt"], root.Rows.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Build_FolderRow_UsesRealEntryMetadataWhenPresent()
    {
        var real = Dir("pics") with { Modified = new DateTime(2025, 8, 22), Crc = 0x1234abcd };
        var root = ArchiveHierarchy.Build(Listing(real, File("pics/x.png")));

        var row = root.Rows.Single();
        Assert.True(row.IsDirectory);
        Assert.Equal(new DateTime(2025, 8, 22), row.Modified);
        Assert.Equal<uint?>(0x1234abcd, row.Crc);
    }

    [Fact]
    public void Build_FolderRow_SynthesizesEntryWhenAbsent()
    {
        var root = ArchiveHierarchy.Build(Listing(File("pics/x.png")));

        var row = root.Rows.Single();
        Assert.Equal("pics", row.Name);
        Assert.Equal("pics/", row.Path);
        Assert.True(row.IsDirectory);
        Assert.False(row.IsEncrypted);
        Assert.Null(row.Crc);
    }

    [Fact]
    public void Build_Rows_SortFoldersAndFilesTogetherByName()
    {
        var root = ArchiveHierarchy.Build(Listing(
            File("红.txt"),
            Dir("白"),
            File("蓝.txt"),
            Dir("白 保障卡")));

        // 目录在前、文件在后，各自按名称的码位序（ordinal）比较 —— 与资源管理器 / 参考软件一致。
        Assert.Equal(["白", "白 保障卡", "红.txt", "蓝.txt"], root.Rows.Select(e => e.Name).ToArray());
        Assert.Equal(["白", "白 保障卡"], root.Folders.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void Build_NormalisesSlashesAndEmptySegments()
    {
        var root = ArchiveHierarchy.Build(Listing(
            File("a//b/c.txt"),
            File("/a/d.txt"),
            Dir("a/")));

        var a = Assert.Single(root.Folders);
        Assert.Equal("a", a.Path);
        Assert.Equal(["b", "d.txt"], a.Rows.Select(e => e.Name).ToArray());
        Assert.Equal(["a"], root.Rows.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Build_EmptyArchive_HasRootWithNoRows()
    {
        var root = ArchiveHierarchy.Build(Listing());

        Assert.Empty(root.Rows);
        Assert.Empty(root.Folders);
        Assert.True(root.IsRoot);
    }

    [Fact]
    public void ParentPathOf_CoversRootNestedAndTrailingForms()
    {
        Assert.Equal(string.Empty, ArchiveHierarchy.ParentPathOf("top.txt"));
        Assert.Equal("a", ArchiveHierarchy.ParentPathOf("a/b.txt"));
        Assert.Equal("a/b", ArchiveHierarchy.ParentPathOf("a/b/c/"));
        Assert.Equal(string.Empty, ArchiveHierarchy.ParentPathOf("a//"));
    }

    [Fact]
    public void Build_KeepsEntryIndexForContentReadBack()
    {
        // 缩略图按 Index 回读原始字节，建树不能把它弄丢。
        var root = ArchiveHierarchy.Build(Listing(File("a/b.txt", index: 7)));

        Assert.Equal(7, root.Folders[0].Rows[0].Index);
    }
}
