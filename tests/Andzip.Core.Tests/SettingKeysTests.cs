using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 常规设置的两项取值，以及「双击直接解压」的落盘位置。
/// 后者值得钉住：它决定双击会不会把文件解压到用户没预期的地方。
/// </summary>
public class SettingKeysTests
{
    [Theory]
    [InlineData(ArchiveDoubleClick.OpenInAndzip, "open")]
    [InlineData(ArchiveDoubleClick.ExtractDirectly, "extract")]
    public void DoubleClick_RoundTripsThroughStorage(ArchiveDoubleClick mode, string stored)
    {
        Assert.Equal(stored, mode.ToStorage());
        Assert.Equal(mode, SettingKeys.ParseDoubleClick(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nuke-from-orbit")]
    public void ParseDoubleClick_FallsBackToOpeningTheWindow(string? stored)
    {
        // 认不出来的取值一律退回“打开 Andzip”：那是最保守的行为，不会在用户没同意时就往磁盘上写东西。
        Assert.Equal(ArchiveDoubleClick.OpenInAndzip, SettingKeys.ParseDoubleClick(stored));
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("EXTRACT")]
    [InlineData(" extract ")]
    public void ParseDoubleClick_IsCaseAndWhitespaceInsensitive(string stored)
        => Assert.Equal(ArchiveDoubleClick.ExtractDirectly, SettingKeys.ParseDoubleClick(stored));

    [Fact]
    public void DirectExtractDestination_PutsFilesUnderAFolderNamedAfterTheArchive()
    {
        var archive = Path.Combine(Path.GetTempPath(), "andzip-dst", "集训队证件照.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

        var destination = SettingKeys.DirectExtractDestination(archive);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "andzip-dst", "集训队证件照"), destination);
    }

    [Fact]
    public void DirectExtractDestination_UsesOnlyTheFirstExtension()
    {
        // .tar.gz 这类复合后缀只剥掉最后一段：目标是 "archive.tar"，不是 "archive"。
        // 与「选择解压路径」里同名文件夹那条规则保持一致，不另起一套。
        var destination = SettingKeys.DirectExtractDestination(Path.Combine(Path.GetTempPath(), "archive.tar.gz"));

        Assert.Equal("archive.tar", Path.GetFileName(destination));
    }

    [Fact]
    public void DirectExtractDestination_RejectsEmptyPath()
        => Assert.ThrowsAny<ArgumentException>(() => SettingKeys.DirectExtractDestination(" "));
}
