using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 「查找 → 文件查找」的匹配规则。
/// <para>四条都在界面上看不出来，但每一条都会被"顺手改一下"改坏：
/// 匹配的是完整路径、不区分大小写、空查询不返回整份清单、结果顺序与主列表的排序无关。</para>
/// </summary>
public class ArchiveSearchTests
{
    private static ArchiveEntryInfo File(string path, long size = 10)
        => new(path, NameOf(path), false, size, size, false, "Deflate", null);

    private static ArchiveEntryInfo Dir(string path)
        => new(path + "/", NameOf(path), true, 0, 0, false, null, null);

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static ArchiveListing Listing(params ArchiveEntryInfo[] entries)
        => new("test.zip", entries, entries.Count(e => !e.IsDirectory), entries.Count(e => e.IsDirectory), 0, 0, null);

    private static readonly ArchiveListing Sample = Listing(
        Dir("docs"),
        File("docs/readme.md"),
        File("notes/README.txt"),
        File("readme.md"),
        File("assets/logo.png"));

    [Fact]
    public void MatchesTheWholePathNotJustTheName()
    {
        // 搜目录名应当把整棵子树带出来 —— 只比文件名的话，"docs" 这个词什么都搜不到，
        // 而用户看着左侧那棵树里的 docs，会认为搜索坏了。
        Assert.Equal(2, ArchiveSearch.Find(Sample, "docs").Count);
        Assert.Single(ArchiveSearch.Find(Sample, "logo"));
    }

    [Fact]
    public void MatchingIgnoresCase()
    {
        Assert.Equal(
            ArchiveSearch.Find(Sample, "readme").Count,
            ArchiveSearch.Find(Sample, "README").Count);

        Assert.Contains("notes/README.txt", ArchiveSearch.Find(Sample, "readme").Select(e => e.Path));
    }

    [Fact]
    public void BlankQueryReturnsNothingInsteadOfTheWholeArchive()
    {
        // 整份清单主列表里已经摆着了。搜索框空着时再抄一遍，用户会以为"还没搜就出结果"是 bug。
        Assert.Empty(ArchiveSearch.Find(Sample, ""));
        Assert.Empty(ArchiveSearch.Find(Sample, "   "));
    }

    [Fact]
    public void SurroundingSpacesAreNotPartOfTheWord()
    {
        // 复制文件名时最常带进来的就是一个尾随空格：按字面匹配会搜不到，而用户看不出为什么。
        Assert.NotEmpty(ArchiveSearch.Find(Sample, "  readme.md  "));
    }

    [Fact]
    public void ResultsAreOrderedByPathSoTheSameWordAlwaysLooksTheSame()
    {
        Assert.Equal(
            ["docs/readme.md", "notes/README.txt", "readme.md"],
            ArchiveSearch.Find(Sample, "readme").Select(e => e.Path).ToArray());
    }

    [Fact]
    public void NoMatchIsEmptyRatherThanAnError()
    {
        Assert.Empty(ArchiveSearch.Find(Sample, "没有这个文件"));
    }
}
