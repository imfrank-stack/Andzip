using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 智能解压的三情形判定与落点。
/// <para>
/// 这几条值得钉死：判错一个情形就会把文件解到用户没预期的地方，
/// 或者在已有目录上盖一层同名目录 —— 两种都很难看，而且都不会报错。
/// </para>
/// </summary>
public class ExtractionLayoutTests
{
    [Fact]
    public void SingleFileAtRoot_IsSingleFile()
    {
        var shape = ExtractionLayout.Inspect(Listing("photo.jpg"));

        Assert.Equal(ArchiveTopLevel.SingleFile, shape.Kind);
        // 顶层名要带出来：建同名文件夹时用的是<strong>文件</strong>的名字，不是压缩包的名字。
        Assert.Equal("photo.jpg", shape.RootName);
    }

    [Fact]
    public void EverythingUnderOneFolder_IsSingleFolder()
        => Assert.Equal(ArchiveTopLevel.SingleFolder, ExtractionLayout.Inspect(
            Listing("docs/", "docs/a.txt", "docs/sub/b.txt")).Kind);

    [Fact]
    public void FolderEntryWithoutChildren_StillCountsAsSingleFolder()
    {
        // 空目录包：唯一条目本身就是目录（以 '/' 结尾），不能因为"没有深层条目"就判成文件。
        Assert.Equal(ArchiveTopLevel.SingleFolder, ExtractionLayout.Inspect(Listing("empty/")).Kind);
    }

    [Fact]
    public void SeveralTopLevelEntries_AreMixed()
        => Assert.Equal(ArchiveTopLevel.Mixed, ExtractionLayout.Inspect(Listing("a.txt", "b.txt")).Kind);

    [Fact]
    public void FolderAndFileSideBySide_AreMixed()
        => Assert.Equal(ArchiveTopLevel.Mixed, ExtractionLayout.Inspect(
            Listing("docs/", "docs/a.txt", "readme.md")).Kind);

    [Fact]
    public void EmptyArchive_IsMixed()
    {
        // 没东西可摆，但也不该把文件铺在压缩包所在目录里 —— 交给一个明确的袋子最安全。
        Assert.Equal(ArchiveTopLevel.Mixed, ExtractionLayout.Inspect(Listing()).Kind);
    }

    [Fact]
    public void RootNameIsOnlySetForTheSingleFileCase()
    {
        Assert.Null(ExtractionLayout.Inspect(Listing("docs/a.txt")).RootName);
        Assert.Null(ExtractionLayout.Inspect(Listing("a.txt", "b.txt")).RootName);
    }

    [Fact]
    public void RootPathsAreNormalisedAcrossSeparatorsAndLeadingSlashes()
    {
        // 清单里偶尔见到 '\' 分隔与开头的 '/'；归一之后仍然只有 docs 一个顶层名。
        var listing = new ArchiveListing(
            "a.zip",
            [
                Entry("/docs/a.txt"),
                Entry(@"docs\sub\b.txt"),
                Entry("docs/"),
            ],
            FileCount: 2, DirectoryCount: 1, TotalSize: 0, TotalCompressedSize: 0, Comment: null);

        Assert.Equal(ArchiveTopLevel.SingleFolder, ExtractionLayout.Inspect(listing).Kind);
    }

    [Fact]
    public void SingleFileLandsInTheArchiveFolderNotASubfolder()
    {
        var archive = Path.Combine(Path.GetTempPath(), "az-layout", "photo.jpg.gz");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-layout"),
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.SingleFile, "photo.jpg")));
    }

    [Fact]
    public void SingleFileToFolderUsesTheFileNameNotTheArchiveName()
    {
        // 开「单个文件也解压到新建文件夹」时，文件夹叫 photo 而不是 photo.jpg.gz ——
        // 用的是包里那个文件的名字（去掉扩展名），套上压缩包的名字会把 .gz 也带进目录名。
        var archive = Path.Combine(Path.GetTempPath(), "az-layout", "photo.jpg.gz");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-layout", "photo"),
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.SingleFile, "photo.jpg"), singleFileToFolder: true));
    }

    [Fact]
    public void SingleFileToFolderStillAvoidsAnExistingName()
    {
        var directory = Directory.CreateTempSubdirectory("az-layout-").FullName;
        var archive = Path.Combine(directory, "report.txt.gz");
        Directory.CreateDirectory(Path.Combine(directory, "report"));

        var destination = ExtractionLayout.Destination(
            archive, Shape(ArchiveTopLevel.SingleFile, "report.txt"), singleFileToFolder: true);

        Assert.Equal(Path.Combine(directory, "report (2)"), destination);
    }

    [Fact]
    public void SingleFolderLandsInTheArchiveFolderSoNoDoubleNesting()
    {
        // 包里是 docs/…，包又叫 docs.zip：解到当前目录得到 docs\，
        // 而不是 docs\docs\ —— 用户抱怨的正是后者。
        var archive = Path.Combine(Path.GetTempPath(), "az-layout", "docs.zip");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-layout"),
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.SingleFolder)));
    }

    [Fact]
    public void MixedLandsInAFolderNamedAfterTheArchive()
    {
        var archive = Path.Combine(Path.GetTempPath(), "az-layout", "stuff.zip");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-layout", "stuff"),
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.Mixed)));
    }

    [Fact]
    public void MixedKeepsOnlyTheLastExtensionOfACompoundSuffix()
    {
        var archive = Path.Combine(Path.GetTempPath(), "az-layout", "bundle.tar.gz");

        Assert.Equal("bundle.tar", Path.GetFileName(ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.Mixed))));
    }

    [Fact]
    public void SingleFileToFolderDoesNotAffectTheOtherTwoShapes()
    {
        var archive = Path.Combine(Path.GetTempPath(), "az-layout", "docs.zip");

        Assert.Equal(
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.SingleFolder)),
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.SingleFolder), singleFileToFolder: true));
        Assert.Equal(
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.Mixed)),
            ExtractionLayout.Destination(archive, Shape(ArchiveTopLevel.Mixed), singleFileToFolder: true));
    }

    [Fact]
    public void MixedAppendsAnIndexInsteadOfReusingAnExistingFolder()
    {
        var directory = Directory.CreateTempSubdirectory("az-layout-").FullName;
        var taken = Path.Combine(directory, "report");
        Directory.CreateDirectory(taken);

        var actual = ShellTasks.UniqueFolder(taken);

        Assert.Equal(Path.Combine(directory, "report (2)"), actual);
        Assert.True(Directory.Exists(taken), "已有目录不能被拿去做候选路径");
    }

    [Fact]
    public void MixedSkipsANameTakenByAFileToo()
    {
        // 一个与目标目录同名的**文件**同样会让建目录失败，所以它也算占用。
        var directory = Directory.CreateTempSubdirectory("az-layout-").FullName;
        var blocker = Path.Combine(directory, "notes");
        File.WriteAllText(blocker, "not a folder");

        Assert.Equal(Path.Combine(directory, "notes (2)"), ShellTasks.UniqueFolder(blocker));
    }

    [Fact]
    public void MixedIndexWalksPastSeveralTakenNames()
    {
        var directory = Directory.CreateTempSubdirectory("az-layout-").FullName;
        Directory.CreateDirectory(Path.Combine(directory, "photos"));
        Directory.CreateDirectory(Path.Combine(directory, "photos (2)"));

        Assert.Equal(
            Path.Combine(directory, "photos (3)"),
            ShellTasks.UniqueFolder(Path.Combine(directory, "photos")));
    }

    [Fact]
    public void MixedKeepsDotsInsideAFolderNameWhenIndexing()
    {
        // 目录名里的点不是扩展名：my.app 撞名后要得到 "my.app (2)"，不是 "my (2).app"。
        var directory = Directory.CreateTempSubdirectory("az-layout-").FullName;
        var taken = Path.Combine(directory, "my.app");
        Directory.CreateDirectory(taken);

        Assert.Equal(Path.Combine(directory, "my.app (2)"), ShellTasks.UniqueFolder(taken));
    }

    [Fact]
    public void UniqueFolderLeavesAFreeNameAlone()
    {
        var directory = Directory.CreateTempSubdirectory("az-layout-").FullName;
        var preferred = Path.Combine(directory, "fresh");

        Assert.Equal(preferred, ShellTasks.UniqueFolder(preferred));
    }

    private static ArchiveShape Shape(ArchiveTopLevel kind, string? rootName = null) => new(kind, rootName);

    private static ArchiveEntryInfo Entry(string path)
        => new(path, Path.GetFileName(path.TrimEnd('/', '\\')), path.EndsWith('/') || path.EndsWith('\\'),
            Size: 0, CompressedSize: 0, IsEncrypted: false, Method: null, Modified: null);

    private static ArchiveListing Listing(params string[] paths)
        => new("archive.zip", [.. paths.Select(Entry)],
            FileCount: paths.Count(p => !p.EndsWith('/')),
            DirectoryCount: paths.Count(p => p.EndsWith('/')),
            TotalSize: 0, TotalCompressedSize: 0, Comment: null);
}
