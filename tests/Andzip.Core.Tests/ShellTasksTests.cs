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
}
