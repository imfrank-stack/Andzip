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

    // ---------------------------------------------------- 三个门面（界面只调这三个）

    /// <summary>
    /// 界面传"要删谁"，不传"要留谁" —— 留的那份由这里算。理由写在
    /// <see cref="ArchiveUpdateSettings.KeepIndices"/> 那段：少列一个就是悄悄删掉一个，
    /// 那种错误不该有机会从界面代码里发生。
    /// </summary>
    [Fact]
    public void TheRemoveFacadeTurnsDeleteMeIntoKeepTheRest()
    {
        var zip = Seed("facade-del.zip", "ZIP", ("a.txt", "AAA"), ("b.txt", "BBB"), ("c.txt", "CCC"));

        ArchiveUpdater.Remove(zip, [(int)IndexOf(zip, "b.txt")]);

        var entries = ReadWithDotNet(zip);
        Assert.Equal("AAA", entries["a.txt"]);
        Assert.Equal("CCC", entries["c.txt"]);
        Assert.False(entries.ContainsKey("b.txt"));
    }

    [Fact]
    public void TheAddFacadeKeepsEverythingElseAndTheArchiveComment()
    {
        var zip = Seed("facade-add.zip", "ZIP", ("a.txt", "AAA"));
        ZipComment.Write(zip, "加成员之前就在的那一句");

        ArchiveUpdater.Add(zip, [new ArchiveAddedMember("b.txt", SourcePath: PathOf("src", "a.txt"))]);

        var entries = ReadWithDotNet(zip);
        Assert.Equal("AAA", entries["a.txt"]);
        Assert.Equal("AAA", entries["b.txt"]);
        Assert.Equal("加成员之前就在的那一句", ZipComment.Read(zip));
    }

    /// <summary>
    /// 改名：内容照搬、原件的时间与属性带过去。
    /// <para>不带时间的话，屏幕上就是"改个名字，修改日期变成今天" —— 这是我们自己的临时文件的时间，
    /// 不是那个成员的。这条断言量的是清单里读回来的那一栏。</para>
    /// </summary>
    [Fact]
    public void TheRenameFacadeKeepsTheContentAndTheOriginalTimestamp()
    {
        var zip = Seed("facade-rename.zip", "ZIP", ("old.txt", "hello"), ("keep.txt", "untouched"));
        var index = (int)IndexOf(zip, "old.txt");
        var original = Member(zip, "old.txt");
        Assert.NotNull(original.Modified);

        ArchiveUpdater.Rename(zip, index, "new.txt");

        var entries = ReadWithDotNet(zip);
        Assert.Equal("hello", entries["new.txt"]);
        Assert.Equal("untouched", entries["keep.txt"]);
        Assert.False(entries.ContainsKey("old.txt"));

        var renamed = Member(zip, "new.txt");
        Assert.Equal(original.Modified, renamed.Modified);
    }

    /// <summary>
    /// 目录改名做不了，而且<strong>当场说清楚、一个字节都不动</strong>：
    /// 半个树留在包里比拒绝更糟（目录改名要连底下每一个成员的路径一起改）。
    /// </summary>
    [Fact]
    public void RenamingADirectoryIsRefusedWithoutTouchingTheArchive()
    {
        // 手工造一个带目录项的包：ArchiveCreator 收的是单个文件，没有"目录"这种东西
        var zip = PathOf("facade-dir.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("dir/");
            using (var stream = archive.CreateEntry("dir/inner.txt").Open())
            {
                stream.Write("X"u8);
            }

            using (var stream = archive.CreateEntry("keep.txt").Open())
            {
                stream.Write("K"u8);
            }
        }

        var directory = ArchiveReader.Read(zip).Entries.First(e => e.IsDirectory);
        var before = File.ReadAllBytes(zip);

        var ex = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveUpdater.Rename(zip, directory.Index, "renamed"));

        Assert.Equal(ArchiveConstraintReason.DirectoryRenameNotSupported, ex.Reason);
        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Fact]
    public void AnUnknownIndexIsRefusedRatherThanSilentlyDoingNothing()
    {
        var zip = Seed("facade-index.zip", "ZIP", ("a.txt", "AAA"));

        Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveUpdater.Rename(zip, 99, "x.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveUpdater.Remove(zip, [99]));
        Assert.Equal("AAA", ReadWithDotNet(zip)["a.txt"]);
    }

    /// <summary>
    /// 格式改不动时，三个门面都在动手之前拒绝 —— 而不是让引擎在半个包上失败。
    /// 种子用 GZ：它<strong>能创建</strong>（所以不是"格式不支持创建"那条），只是改不动。
    /// </summary>
    [Fact]
    public void EveryFacadeRefusesAFormatThatCannotBeRewritten()
    {
        var gz = PathOf("only.gz");
        ArchiveCreator.Create(
            [Source("note.txt", "hello")],
            new ArchiveCreationSettings(
                gz,
                CompressionLevelOption.Default,
                Format: ArchiveFormatOption.FromId("GZ"),
                IncludeRootFolder: false));

        var before = File.ReadAllBytes(gz);
        Assert.False(ArchiveUpdater.CanUpdate(ArchiveReader.Read(gz).FormatName));

        var add = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveUpdater.Add(gz, [new ArchiveAddedMember("x.txt", Data: "x"u8.ToArray())]));
        var remove = Assert.Throws<ArchiveConstraintException>(() => ArchiveUpdater.Remove(gz, [0]));
        var rename = Assert.Throws<ArchiveConstraintException>(() => ArchiveUpdater.Rename(gz, 0, "y.txt"));

        Assert.Equal(ArchiveConstraintReason.ArchiveNotEditable, add.Reason);
        Assert.Equal(ArchiveConstraintReason.ArchiveNotEditable, remove.Reason);
        Assert.Equal(ArchiveConstraintReason.ArchiveNotEditable, rename.Reason);
        Assert.Equal(before, File.ReadAllBytes(gz));
    }

    [Fact]
    public void CanUpdateAnswersForTheFormatsWeClaimToEdit()
    {
        Assert.True(ArchiveUpdater.CanUpdate("zip"));
        Assert.True(ArchiveUpdater.CanUpdate("ZIP"));      // 引擎给的是小写，但别让大小写决定成败
        Assert.True(ArchiveUpdater.CanUpdate("7z"));
        Assert.True(ArchiveUpdater.CanUpdate("tar"));

        Assert.False(ArchiveUpdater.CanUpdate("gzip"));
        Assert.False(ArchiveUpdater.CanUpdate("rar"));
        Assert.False(ArchiveUpdater.CanUpdate(null));
    }

    /// <summary>建一个源<strong>目录</strong>并写进里面那几个文件，返回目录路径。</summary>
    private string SourceDir(string relative, params (string Name, string Text)[] files)
    {
        var full = PathOf("src", relative);
        Directory.CreateDirectory(full);
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(full, name), text);
        }

        return full;
    }

    /// <summary>
    /// 按输入项建包。<see cref="Seed"/> 收的是平铺的文件名（<c>ArchiveInputItem.Name</c> 只有叶子名），
    /// 造不出 <c>docs/old.txt</c> 这种带前缀的结构，所以目录那几条用例走这里。
    /// </summary>
    private string SeedItems(string archiveName, string formatId, params string[] sourcePaths)
    {
        var output = PathOf(archiveName);
        ArchiveCreator.Create(
            [.. sourcePaths.Select(ArchiveInputItem.FromPath)],
            new ArchiveCreationSettings(
                output,
                CompressionLevelOption.Default,
                Format: ArchiveFormatOption.FromId(formatId),
                IncludeRootFolder: false));
        return output;
    }

    /// <summary>
    /// 加进来的一项若是目录，底下的整棵树要一起进来。少这一条，从剪贴板粘一个文件夹
    /// 会得到包里一个空目录 —— 那种"操作没报错、内容不在"正是最难发现的一类。
    /// </summary>
    [Fact]
    public void AddingADirectoryBringsItsWholeTree()
    {
        var zip = Seed("add-tree.zip", "ZIP", ("keep.txt", "K"));

        ArchiveUpdater.Add(zip, [new ArchiveAddedMember("tree", SourcePath: SourceDir("tree", ("inner-a.txt", "A"), ("inner-b.txt", "B")))]);

        var entries = ReadWithDotNet(zip);
        Assert.Equal("K", entries["keep.txt"]);
        Assert.Equal("A", entries["tree/inner-a.txt"]);
        Assert.Equal("B", entries["tree/inner-b.txt"]);
    }

    /// <summary>
    /// 默认<strong>不覆盖</strong>：同名的那个老成员原样留着，新加的叫同一个名字也是两条
    /// （引擎允许，也确实是"我什么都没删"）。这条断言把"默认关闭"钉住 ——
    /// 哪天有人顺手把它改成 true，界面上那句"要替换吗"就变成了空话。
    /// </summary>
    [Fact]
    public void AddingWithoutReplacingKeepsBothUnderTheSameName()
    {
        var zip = Seed("add-noclobber.zip", "ZIP", ("same.txt", "OLD"));

        ArchiveUpdater.Add(zip, [new ArchiveAddedMember("same.txt", Data: "NEW"u8.ToArray())]);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(2, archive.Entries.Count(e => e.FullName == "same.txt"));
    }

    [Fact]
    public void AddingWithReplacementLeavesExactlyOneUnderThatName()
    {
        var zip = Seed("add-clobber.zip", "ZIP", ("same.txt", "OLD"), ("keep.txt", "K"));

        ArchiveUpdater.Add(
            zip,
            [new ArchiveAddedMember("same.txt", Data: "NEW"u8.ToArray())],
            replaceExisting: true);

        var entries = ReadWithDotNet(zip);
        Assert.Equal("NEW", entries["same.txt"]);
        Assert.Equal("K", entries["keep.txt"]);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(1, archive.Entries.Count(e => e.FullName == "same.txt"));
    }

    /// <summary>
    /// 替换的判据是"路径被占住"，不是"名字相同"：加进来一个目录叫 <c>docs</c>，
    /// 包里原有的 <c>docs/old.txt</c> 也要划掉 —— 否则新旧混在一层里，
    /// 用户以为换了一整个文件夹，实际是把两个文件夹叠在了一起。
    /// <para>动手之前先确认那几条真的在包内（否则这条断言量的是空气）。</para>
    /// </summary>
    [Fact]
    public void ReplacingADirectoryAlsoDropsWhatWasUnderIt()
    {
        var zip = SeedItems(
            "add-clobber-dir.zip",
            "ZIP",
            SourceDir("docs", ("old.txt", "OLD"), ("old2.txt", "OLD2")),
            Source("keep.txt", "K").FullPath);

        var before = ReadWithDotNet(zip);
        Assert.True(before.ContainsKey("docs/old.txt"));
        Assert.True(before.ContainsKey("docs/old2.txt"));

        ArchiveUpdater.Add(
            zip,
            [new ArchiveAddedMember("docs", SourcePath: SourceDir("replacement", ("new.txt", "N")))],
            replaceExisting: true);

        var entries = ReadWithDotNet(zip);
        Assert.False(entries.ContainsKey("docs/old.txt"));
        Assert.False(entries.ContainsKey("docs/old2.txt"));
        Assert.Equal("N", entries["docs/new.txt"]);
        Assert.Equal("K", entries["keep.txt"]);
    }

    /// <summary>
    /// 「将文件复制到剪贴板」落地那一步：<strong>只</strong>解列出的那几个，包内路径原样保留。
    /// 顺手把整包解出来（或者把路径压平）都会让剪贴板里的东西和用户刚看见的树对不上。
    /// </summary>
    [Fact]
    public void ExtractMembersTakesOnlyWhatWasAskedForAndKeepsItsPath()
    {
        var zip = SeedItems(
            "members.zip",
            "ZIP",
            SourceDir("docs", ("a.txt", "A"), ("b.txt", "B")),
            Source("top.txt", "T").FullPath);

        var index = ArchiveReader.Read(zip).Entries.Single(e => e.Path == "docs/a.txt").Index;
        var target = PathOf("members-out");

        var result = ArchiveExtractor.ExtractMembers(zip, [index], target);

        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "docs", "a.txt")));
        Assert.False(File.Exists(Path.Combine(target, "docs", "b.txt")));
        Assert.False(File.Exists(Path.Combine(target, "top.txt")));
        Assert.Equal([Path.Combine(target, "docs", "a.txt")], result.ExtractedFiles);
        Assert.Equal(1, result.FileCount);
    }

    /// <summary>只给目录那一项时，它自己也落地 —— 空目录也要出现，
    /// 否则"我复制了一个空文件夹"粘出来什么都没有。</summary>
    [Fact]
    public void ExtractMembersMaterialisesTheDirectoryItself()
    {
        var zip = PathOf("members-dir.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("empty/");
            using (var stream = archive.CreateEntry("empty/inner.txt").Open())
            {
                stream.Write("I"u8);
            }
        }

        var directory = ArchiveReader.Read(zip).Entries.Single(e => e.IsDirectory);
        var target = PathOf("members-dir-out");

        ArchiveExtractor.ExtractMembers(zip, [directory.Index], target);

        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.False(File.Exists(Path.Combine(target, "empty", "inner.txt")));
    }

    [Fact]
    public void ExtractMembersRefusesAnIndexThatIsNotThere()
    {
        var zip = Seed("members-bad.zip", "ZIP", ("a.txt", "A"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArchiveExtractor.ExtractMembers(zip, [99], PathOf("members-bad-out")));
    }
}
