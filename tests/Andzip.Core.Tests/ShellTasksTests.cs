using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 右键任务的命令行解析与落点规则。这两件事决定「右键 → 直接解压」到底把文件放到哪儿，
/// 所以路径规则必须在没有注册表、没有 GUI 的情况下钉死。
/// </summary>
public class ShellTasksTests
{
    [Theory]
    [InlineData("/task=open", ShellTask.Open)]
    [InlineData("/task=extract", ShellTask.Extract)]
    [InlineData("/task=extract-delete", ShellTask.ExtractDelete)]
    [InlineData("/task=compress-zip", ShellTask.CompressZip)]
    [InlineData("/task=compress-dialog", ShellTask.CompressDialog)]
    [InlineData("/task=nonsense", ShellTask.Open)]
    public void TryParse_RecognisesEveryVerb(string switchToken, ShellTask expected)
    {
        Assert.True(ShellTasks.TryParse(["C:\\a.7z", switchToken], out var task));
        Assert.Equal(expected, task);
    }

    [Theory]
    [InlineData("/task=auto-extract")]
    [InlineData("/task=extract-here")]
    public void TryParse_StillAcceptsTheRetiredExtractVerbs(string switchToken)
    {
        // 0.9.12 把「智能解压」「解压到当前文件夹」并成了一条「解压」。动词键在写注册表时会被删掉，
        // 但用户桌面上可能还留着指向老命令行的快捷方式 —— 认成现在这一条，比退回"打开"更合预期。
        Assert.True(ShellTasks.TryParse(["C:\\a.7z", switchToken], out var task));
        Assert.Equal(ShellTask.Extract, task);
    }

    [Fact]
    public void TryParse_StillAcceptsTheLegacyBareSwitch()
    {
        // v0.9.4 注册出去的命令带的是裸 /extract，不能因为换了写法就把老关联变成“只开窗口”。
        Assert.True(ShellTasks.TryParse(["C:\\a.7z", "/extract"], out var task));
        Assert.Equal(ShellTask.Extract, task);
    }

    [Fact]
    public void TryParse_ReportsAbsentWhenOnlyAPathIsGiven()
    {
        // 没有任务开关 = 双击，交给「常规 → 双击行为」决定，这里不越权。
        Assert.False(ShellTasks.TryParse(["C:\\a.7z"], out _));
        Assert.False(ShellTasks.TryParse(null, out _));
    }

    [Fact]
    public void Switch_RoundTripsThroughParse()
    {
        foreach (ShellTask task in Enum.GetValues<ShellTask>())
        {
            Assert.True(ShellTasks.TryParse([ShellTasks.Switch(task)], out var parsed));
            Assert.Equal(task, parsed);
        }
    }

    [Theory]
    [InlineData(ShellTask.Extract, true)]
    [InlineData(ShellTask.ExtractDelete, true)]
    [InlineData(ShellTask.Open, false)]
    [InlineData(ShellTask.CompressZip, false)]
    [InlineData(ShellTask.CompressDialog, false)]
    public void IsExtraction_CoversExactlyTheTwoExtractTasks(ShellTask task, bool expected)
    {
        // 分发处（ShellTaskRunner）就靠这个判断走不走 DirectExtract，
        // 漏一条就是"右键解压点了没反应"。
        Assert.Equal(expected, ShellTasks.IsExtraction(task));
    }

    [Fact]
    public void SiblingFolder_NestsUnderAFolderNamedAfterTheArchive()
    {
        var archive = Path.Combine(Path.GetTempPath(), "az-shell", "集训队证件照.7z");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-shell", "集训队证件照"),
            ShellTasks.SiblingFolder(archive));
    }

    [Fact]
    public void SiblingFolder_UsesOnlyTheFirstExtension()
    {
        // .tar.gz 这类复合后缀只剥掉最后一段：目标是 "archive.tar"，不是 "archive"。
        // 与「选择解压路径」里同名文件夹那条规则保持一致，不另起一套。
        var archive = Path.Combine(Path.GetTempPath(), "archive.tar.gz");

        Assert.Equal("archive.tar", Path.GetFileName(ShellTasks.SiblingFolder(archive)));
    }

    [Fact]
    public void ArchivePathFor_StripsTheFileExtension()
    {
        var source = Path.Combine(Path.GetTempPath(), "az-shell", "photo.jpg");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-shell", "photo.zip"),
            ShellTasks.ArchivePathFor(source, ".zip", isDirectory: false));
    }

    [Fact]
    public void ArchivePathFor_KeepsDotsInsideAFolderName()
    {
        // 文件夹叫 my.app 时不能把 ".app" 当扩展名剥掉，否则包名与文件夹名对不上。
        var source = Path.Combine(Path.GetTempPath(), "az-shell", "my.app");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-shell", "my.app.7z"),
            ShellTasks.ArchivePathFor(source, ".7z", isDirectory: true));
    }

    [Fact]
    public void UniqueArchivePath_AppendsAnIndexInsteadOfOverwriting()
    {
        var directory = Directory.CreateTempSubdirectory("az-shell-").FullName;
        var preferred = Path.Combine(directory, "report.zip");
        File.WriteAllText(preferred, "existing archive");

        var actual = ShellTasks.UniqueArchivePath(preferred);

        Assert.NotEqual(preferred, actual);
        Assert.Equal("report (2).zip", Path.GetFileName(actual));
        Assert.True(File.Exists(preferred), "已存在的包不能被拿去做候选路径");
    }

    [Fact]
    public void UniqueArchivePath_LeavesAFreeNameAlone()
    {
        var directory = Directory.CreateTempSubdirectory("az-shell-").FullName;
        var preferred = Path.Combine(directory, "fresh.zip");

        Assert.Equal(preferred, ShellTasks.UniqueArchivePath(preferred));
    }

    // ------------------------------------------------------- 建议包名（对话框打开时填的那个）

    [Fact]
    public void SuggestedArchivePath_NamesTheArchiveAfterASingleInput()
    {
        // 用户选中一个东西要压，得到的包就该叫那个东西。「新建压缩.zip」是没人读得懂的名字。
        var file = Path.Combine(Path.GetTempPath(), "az-name", "photo.jpg");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "az-name", "photo.zip"),
            ShellTasks.SuggestedArchivePath([file], ".zip", @"D:\elsewhere", "新建压缩"));
    }

    [Fact]
    public void SuggestedArchivePath_UsesTheFallbackStemForSeveralInputs()
    {
        var directory = Directory.CreateTempSubdirectory("az-name-").FullName;
        var inputs = new[] { Path.Combine(directory, "a.txt"), Path.Combine(directory, "b.txt") };

        Assert.Equal(Path.Combine(directory, "新建压缩.zip"),
            ShellTasks.SuggestedArchivePath(inputs, ".zip", @"D:\elsewhere", "新建压缩"));
    }

    [Fact]
    public void SuggestedArchivePath_LeavesTheFallbackDirectoryWhenInputsAreScattered()
    {
        // 两个输入不在同一个目录下就没有"当前位置"这回事，只能退回调用方给的兜底。
        var inputs = new[] { @"C:\one\a.txt", @"D:\two\b.txt" };

        Assert.Equal(Path.Combine(@"D:\elsewhere", "新建压缩.zip"),
            ShellTasks.SuggestedArchivePath(inputs, ".zip", @"D:\elsewhere", "新建压缩"));
    }

    [Fact]
    public void SuggestedArchivePath_FallsBackWhenThereIsNoInputYet()
    {
        Assert.Equal(Path.Combine(@"D:\elsewhere", "新建压缩.7z"),
            ShellTasks.SuggestedArchivePath([], ".7z", @"D:\elsewhere", "新建压缩"));
    }

    [Fact]
    public void CommonParent_ReportsTheSharedDirectoryOnlyWhenThereIsOne()
    {
        var directory = Directory.CreateTempSubdirectory("az-parent-").FullName;

        Assert.Equal(directory, ShellTasks.CommonParent(
            [Path.Combine(directory, "a.txt"), Path.Combine(directory, "b.txt")]));

        Assert.Null(ShellTasks.CommonParent([@"C:\dir\a.txt", @"C:\other\b.txt"]));
        Assert.Null(ShellTasks.CommonParent([]));
    }

    // ---------------------------------------------------------------- 拖进来的路径

    [Fact]
    public void RouteDroppedPaths_OpensASingleArchiveLikeRightClickOpenDoes()
    {
        // 拖一个包进窗口，和右键「用 Andzip 打开」应当是同一条任务 —— 各写一套判断迟早分叉。
        Assert.Equal(ShellTask.Open, ShellTasks.RouteDroppedPaths([@"C:\x\box.zip"]));
        Assert.Equal(ShellTask.Open, ShellTasks.RouteDroppedPaths([@"C:\x\photos.7z"]));
    }

    [Fact]
    public void RouteDroppedPaths_CompressesAnythingThatIsNotASingleArchive()
    {
        Assert.Equal(ShellTask.CompressDialog, ShellTasks.RouteDroppedPaths([@"C:\x\note.txt"]));
        Assert.Equal(ShellTask.CompressDialog, ShellTasks.RouteDroppedPaths([@"C:\x\photos.zip", @"C:\y\more.zip"]));
        Assert.Equal(ShellTask.CompressDialog, ShellTasks.RouteDroppedPaths([]));

        // 文件夹长得像包也不算：那是"要压的东西"，不是"要看的东西"。
        var directory = Directory.CreateTempSubdirectory("az-drop-");
        try
        {
            var folder = Path.Combine(directory.FullName, "photos.zip");
            Directory.CreateDirectory(folder);
            Assert.Equal(ShellTask.CompressDialog, ShellTasks.RouteDroppedPaths([folder]));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
