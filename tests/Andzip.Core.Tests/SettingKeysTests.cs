using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 常规设置的两项取值。认不出来的取值一律退回「打开 Andzip」，
/// 那是最保守的行为 —— 不会在用户没同意时就往磁盘上写东西。
/// <para>解压落点的规则在 <see cref="ShellTasksTests"/>：它属于右键任务，不属于设置。</para>
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

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("open", false)]
    [InlineData("garbage", false)]
    [InlineData("extract", true)]
    [InlineData("EXTRACT", true)]
    [InlineData(" extract ", true)]
    public void ShouldExtractDirectly_HonoursTheSetting(string? stored, bool expected)
    {
        // 注册表里双击只会调用 shell\open\command，行为全靠这个判断 —— 不能再依赖动词是否存在。
        Assert.Equal(expected, SettingKeys.ShouldExtractDirectly(stored));
    }
}
