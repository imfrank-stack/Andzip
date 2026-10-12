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

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("rar")]
    [InlineData("iso")]
    public void CompressionSuppression_CoversTheArchiveExtensions(string extension)
    {
        Assert.Contains(extension, FileAssociationCatalog.CompressionSuppressedExtensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompressionSuppression_NeverCoversExe()
    {
        // exe 在我们眼里是自解压包，在用户眼里就是"想压小一点的安装文件"。
        // 把它一起屏蔽掉，等于把一条常用命令从用户手里拿走。
        Assert.DoesNotContain("exe", FileAssociationCatalog.CompressionSuppressedExtensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompressionSuppressionQuery_IsTheFormExplorerParses()
    {
        // 这个形状是在本机实测出来的：往 HKCU\Software\Classes\*\shell\Andzip.compress-zip 写
        // 这样一条 AppliesTo，box.zip 的右键菜单里就没有「压缩为 ZIP」，note.txt 上还在。
        // 写法一旦改动（漏掉 NOT、漏掉点号），动词要么不生效要么对所有文件都消失。
        var query = FileAssociationCatalog.CompressionSuppressionQuery();

        Assert.StartsWith("NOT (System.FileExtension:=.", query, StringComparison.Ordinal);
        Assert.EndsWith(")", query, StringComparison.Ordinal);
        Assert.Contains(" OR ", query, StringComparison.Ordinal);
        Assert.Equal(
            FileAssociationCatalog.CompressionSuppressedExtensions.Count,
            query.Split(" OR ", StringSplitOptions.None).Length);
    }

    [Fact]
    public void CompressionSuppressionQuery_ListsEverySuppressedExtensionExactlyOnce()
    {
        var query = FileAssociationCatalog.CompressionSuppressionQuery();
        var named = query
            .Split(" OR ", StringSplitOptions.None)
            .Select(term => term
                .Replace("NOT (", "", StringComparison.Ordinal)
                .Replace("System.FileExtension:=", "", StringComparison.Ordinal)
                .Trim('(', ')', ' ', '.'))
            .ToList();

        Assert.Equal(named.Count, named.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            [.. FileAssociationCatalog.CompressionSuppressedExtensions.OrderBy(e => e, StringComparer.Ordinal)],
            named.OrderBy(e => e, StringComparer.Ordinal));
    }

}
