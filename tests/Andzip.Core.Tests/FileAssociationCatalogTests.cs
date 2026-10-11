using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 文件关联候选目录：候选必须**由引擎能力决定**，而不是照抄一份写死的列表。
/// 这里喂假的扩展名集合来验过滤规则，不依赖真实引擎。
/// </summary>
public class FileAssociationCatalogTests
{
    [Fact]
    public void Supported_DropsFormatsTheEngineCannotRead()
    {
        var groups = FileAssociationCatalog.Supported([".zip", ".tar"]);

        var ids = groups.SelectMany(g => g.Items).Select(i => i.Id).ToList();
        Assert.Contains("zip", ids);
        Assert.Contains("tar", ids);
        Assert.DoesNotContain("7z", ids);
        Assert.DoesNotContain("rar", ids);
    }

    [Fact]
    public void Supported_DropsEmptyGroupsEntirely()
    {
        // 只给 UNIX 组的扩展名，压缩包组应当整组消失而不是留下一个空标题。
        var groups = FileAssociationCatalog.Supported(["gz"]);

        Assert.Single(groups);
        Assert.Equal("TEXT_GROUP_UNIX", groups[0].TextKey);
    }

    [Fact]
    public void Supported_IsInsensitiveToDotsAndCase()
    {
        var withDots = FileAssociationCatalog.Supported([".ZIP", ".TAR.GZ"]);
        var plain = FileAssociationCatalog.Supported(["zip"]);

        // ".TAR.GZ" 归一化后是 "tar.gz"，不在目录里；只有 "zip" 命中。
        Assert.Equal(["zip"], withDots.SelectMany(g => g.Items).Select(i => i.Id).ToArray());
        Assert.Equal(["zip"], plain.SelectMany(g => g.Items).Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Supported_EmptyExtensionSet_ReturnsNoGroups()
    {
        // 引擎缺失时 ReadableExtensions 是空的，关联页应当整个空掉而不是让人勾一堆打不开的东西。
        Assert.Empty(FileAssociationCatalog.Supported([]));
    }

    [Fact]
    public void AnyCatalogExtensionMatchingACompoundEntryCountsAsSupported()
    {
        // RAR 同时登记 rar 与 r00：只读到其中一个也算支持。
        var groups = FileAssociationCatalog.Supported(["r00"]);
        Assert.Contains(groups.SelectMany(g => g.Items), i => i.Id == "rar");
    }

    [Fact]
    public void EveryCatalogExtension_IsActuallyReadableByTheEngine()
    {
        // 目录里写了一个引擎读不了的扩展名，界面上就会出现勾了却打不开的关联。
        var readable = ArchiveFormatOption.OpenableExtensions
            .Select(x => x.TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unsupported = FileAssociationCatalog.All
            .SelectMany(g => g.Items)
            .SelectMany(item => item.Extensions.Where(e => !readable.Contains(e)).Select(e => $"{item.Id}:{e}"))
            .ToList();

        Assert.Empty(unsupported);
    }

    [Fact]
    public void Ids_AreUniqueAcrossGroups()
    {
        var ids = FileAssociationCatalog.All.SelectMany(g => g.Items).Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Find_ResolvesByIdAndReturnsNullForUnknown()
    {
        Assert.Equal("ZIP", FileAssociationCatalog.Find("zip")?.Label);
        Assert.Null(FileAssociationCatalog.Find("nope"));
    }

    [Fact]
    public void DefaultIds_AreSubsetOfCatalogAndExcludeExoticFormats()
    {
        var all = FileAssociationCatalog.All.SelectMany(g => g.Items).Select(i => i.Id).ToHashSet();
        var defaults = FileAssociationCatalog.DefaultIds;

        Assert.NotEmpty(defaults);
        Assert.All(defaults, id => Assert.Contains(id, all));
        // 默认不勾的：exe / 磁盘镜像 / 办公容器这类抢眼的关联。
        Assert.DoesNotContain("exe", defaults);
        Assert.DoesNotContain("img", defaults);
        Assert.DoesNotContain("jar", defaults);
    }
}
