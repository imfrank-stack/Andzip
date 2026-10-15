using System.IO.Compression;
using System.Text;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 往已有的包里加成员、删成员、改名 —— 「编辑」菜单那几项的地基。
/// <para>
/// 验收刻意用<strong>别人写的实现</strong>读回来：.zip 交给 <see cref="System.IO.Compression"/>，
/// 不在本引擎自己的读路径上闭环。"自己写自己读"恰好是这一类改动最看不见的形状 ——
/// 一个把成员名单写错但把清单头写对的实现，只有自己读自己时是绿的。
/// </para>
/// </summary>
public class ArchiveUpdaterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-update-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- helpers ----------

    private string PathOf(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>建一个源文件并返回它的输入项。</summary>
    private ArchiveInputItem Source(string relativePath, string content)
    {
        var full = PathOf("src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return ArchiveInputItem.FromPath(full);
    }

    private string Seed(string archiveName, string formatId, params (string Name, string Text)[] members)
    {
        var output = PathOf(archiveName);
        var inputs = members.Select(m => Source(m.Name, m.Text)).ToList();
        ArchiveCreator.Create(
            inputs,
            new ArchiveCreationSettings(
                output,
                CompressionLevelOption.Default,
                Format: ArchiveFormatOption.FromId(formatId),
                IncludeRootFolder: false));
        return output;
    }

    /// <summary>用 .NET 的 ZIP 实现读，绕开本引擎。</summary>
    private static Dictionary<string, string> ReadWithDotNet(string zipPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[entry.FullName] = Encoding.UTF8.GetString(buffer.ToArray());
        }

        return result;
    }

    private static uint IndexOf(string archivePath, string memberName)
    {
        var index = ArchiveReader.Read(archivePath).Entries.Single(e => e.Path == memberName).Index;
        return index >= 0
            ? (uint)index
            : throw new InvalidOperationException($"{memberName} has no index");
    }

    private static ArchiveEntryInfo Member(string archivePath, string memberName)
        => ArchiveReader.Read(archivePath).Entries.Single(e => e.Path == memberName);

    /// <summary>把整个包解到一个新目录再读那一个成员（Core 没有"读单个成员"的门面）。</summary>
    private string ReadText(string archivePath, string memberName, string? password = null)
    {
        var target = PathOf($"out-{Guid.NewGuid():N}");
        ArchiveExtractor.Extract(new ExtractionSettings(archivePath, target, Password: password));
        return File.ReadAllText(Path.Combine(target, memberName));
    }

    private static void Update(string archivePath, IReadOnlyList<uint> keep, params ArchiveAddedMember[] added)
        => ArchiveUpdater.Update(new ArchiveUpdateSettings(archivePath, keep, added));

    // ---------- tests ----------

    [Fact]
    public void AddingAMemberLeavesTheOnesAlreadyThereAlone()
    {
        var zip = Seed("add.zip", "ZIP", ("a.txt", "AAA"), ("b.txt", "BBB"));

        Update(zip, [0, 1], new ArchiveAddedMember("c.txt", SourcePath: PathOf("src", "a.txt")));

        var entries = ReadWithDotNet(zip);
        Assert.Equal("AAA", entries["a.txt"]);
        Assert.Equal("BBB", entries["b.txt"]);
        Assert.Equal("AAA", entries["c.txt"]);
    }

    [Fact]
    public void DeletingIsJustNotListing()
    {
        var zip = Seed("del.zip", "ZIP", ("a.txt", "AAA"), ("b.txt", "BBB"), ("c.txt", "CCC"));

        Update(zip, [IndexOf(zip, "a.txt"), IndexOf(zip, "c.txt")]);

        var entries = ReadWithDotNet(zip);
        Assert.Equal("AAA", entries["a.txt"]);
        Assert.Equal("CCC", entries["c.txt"]);
        Assert.False(entries.TryGetValue("b.txt", out var gone));
        Assert.Null(gone);
    }

    [Fact]
    public void RenamingIsKeepingTheRestAndAddingOneBackUnderANewName()
    {
        var zip = Seed("ren.zip", "ZIP", ("old.txt", "hello"), ("keep.txt", "untouched"));
        var bytes = Encoding.UTF8.GetBytes("hello");

        Update(
            zip,
            [IndexOf(zip, "keep.txt")],
            new ArchiveAddedMember("new.txt", Data: bytes));

        var entries = ReadWithDotNet(zip);
        Assert.Equal("hello", entries["new.txt"]);
        Assert.Equal("untouched", entries["keep.txt"]);
        Assert.False(entries.ContainsKey("old.txt"));
    }

    /// <summary>
    /// 保留的成员是<strong>照抄存储字节</strong>，不是重压一遍。种子用"仅存储"建，
    /// 所以压缩后大小 == 原始大小；一旦哪天真去重压它，这个数就会变小。
    /// 这条断言的全部意义在于：一个 100 GB 的成员"留着"不该花压 100 GB 的时间。
    /// </summary>
    [Fact]
    public void KeptMembersAreCopiedNotRecompressed()
    {
        var filler = new string('x', 200_000);   // 高度可压，重压一定会变小
        var output = PathOf("copy.zip");
        ArchiveCreator.Create(
            [Source("big.txt", filler)],
            new ArchiveCreationSettings(
                output,
                CompressionLevelOption.Default,
                Format: ArchiveFormatOption.FromId("ZIP"),
                Method: CompressionMethodOption.Store));

        var before = Member(output, "big.txt");
        Assert.Equal(before.Size, before.CompressedSize);

        Update(output, [0]);

        var after = Member(output, "big.txt");
        Assert.Equal(before.CompressedSize, after.CompressedSize);
        Assert.Equal(after.Size, after.CompressedSize);
    }

    [Fact]
    public void TheSameThreeMovesWorkOn7z()
    {
        var seven = Seed("rt.7z", "7Z", ("a.txt", "AAA"), ("b.txt", "BBB"));

        Update(seven, [IndexOf(seven, "a.txt")], new ArchiveAddedMember("c.txt", Data: Encoding.UTF8.GetBytes("CCC")));

        var paths = ArchiveReader.Read(seven).Entries.Select(e => e.Path).ToList();
        Assert.Equal(["a.txt", "c.txt"], paths);
        Assert.Equal("AAA", ReadText(seven, "a.txt"));
        Assert.Equal("CCC", ReadText(seven, "c.txt"));
    }

    /// <summary>
    /// 加密文件头的 7z 没有口令就列不出成员，于是"保留哪些"根本没有答案。
    /// 这里要两件事：改写被拒，以及<strong>原包一个字节都没变</strong> ——
    /// 一个谎报成功的改写是唯一没有救的失败。
    /// </summary>
    [Fact]
    public void AnEncryptedHeaderArchiveRefusesAnUnkeyedUpdateAndChangesNothing()
    {
        var output = PathOf("he.7z");
        ArchiveCreator.Create(
            [Source("secret.txt", "top secret")],
            new ArchiveCreationSettings(
                output,
                CompressionLevelOption.Default,
                Format: ArchiveFormatOption.FromId("7Z"),
                Password: "s3cret",
                EncryptHeaders: true));

        var before = File.ReadAllBytes(output);

        Assert.ThrowsAny<Exception>(() => Update(output, [0]));

        Assert.Equal(before, File.ReadAllBytes(output));
        Assert.Equal("top secret", ReadText(output, "secret.txt", "s3cret"));
    }

    /// <summary>
    /// 一个成员都不列 = 清空整个包。这是"删除即不列出"的直接推论，不是缺陷，
    /// 但它太容易顺手做出来，所以钉在这里：界面绝不能把"我什么都没保留"当成一次普通提交。
    /// </summary>
    [Fact]
    public void ListingNothingEmptiesTheArchive()
    {
        var zip = Seed("empty.zip", "ZIP", ("a.txt", "AAA"), ("b.txt", "BBB"));

        Update(zip, []);

        Assert.Empty(ReadWithDotNet(zip));
    }
}
