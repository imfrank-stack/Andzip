using Andzip.Core.Archive;
using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 排除模式与「设置 → 压缩」的默认值。
/// <para>
/// 排除规则会<strong>静默少打包里的文件</strong>，所以它比一般的字符串处理更值得钉：
/// 一个把 <c>x.bak.txt</c> 也吃进去的模式，用户要到解压时才发现少东西。
/// </para>
/// </summary>
public class CompressionSettingsTests
{
    [Theory]
    [InlineData("report.bak", "*.bak", true)]
    [InlineData("report.bak", "*.BAK", true)]          // Windows 文件名不区分大小写
    [InlineData("report.txt", "*.bak", false)]
    [InlineData("x.bak.txt", "*.bak", false)]          // 整名匹配，不是"包含"
    [InlineData("bak", "*.bak", false)]                // 没有点的名字不该被 *.bak 吃掉
    [InlineData("thumbsdb", "thumbs*", true)]
    [InlineData("a", "?", true)]
    [InlineData("ab", "?", false)]
    [InlineData("notes~1.txt", "notes*.txt", true)]
    [InlineData("backup", "*", true)]
    [InlineData("anything", "", false)]                // 空模式不该把所有文件都排除
    public void IsMatch_FollowsPlainGlobSemantics(string fileName, string pattern, bool expected)
        => Assert.Equal(expected, FileNamePattern.IsMatch(fileName, pattern));

    [Fact]
    public void MatchesAny_EmptyListMatchesNothing()
        => Assert.False(FileNamePattern.MatchesAny([], "a.tmp"));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("*.bak;", 1)]
    [InlineData("*.bak; *.tmp;", 2)]
    [InlineData("*.bak;;*.tmp;  ;*.old", 3)]           // 空段与空白都吞掉
    [InlineData("*.bak;*.bak", 1)]                     // 重复的只留一份
    public void ParseExcludePatterns_ToleratesTheWayPeopleActuallyTypeIt(string? text, int expected)
        => Assert.Equal(expected, ArchiveCreationSettings.ParseExcludePatterns(text).Count);

    [Fact]
    public void ParseExcludePatterns_KeepsCommasInsideAPattern()
    {
        // 分隔符只认分号：Windows 文件名里逗号合法，按逗号切会把一条模式劈成两条不相干的。
        var patterns = ArchiveCreationSettings.ParseExcludePatterns("report,final.txt;*.bak");

        Assert.Equal(["report,final.txt", "*.bak"], patterns);
    }

    [Fact]
    public void CompressionDefaults_RoundTripsThroughSettings()
    {
        var wanted = new CompressionDefaults(
            CompressionLevelOption.Max,
            IncludeRootFolder: false,
            ExcludeThumbsDb: false,
            ExcludePatternText: "*.bak; *.tmp;",
            TestAfterCreation: true,
            KeepProgressWindow: true,
            SettingKeys.OutputFolderMode.Fixed,
            FixedOutputFolder: @"D:\drops");

        var settings = new AppSettings();
        wanted.Save(settings);

        var loaded = CompressionDefaults.Load(settings);

        Assert.Equal(wanted, loaded);
        Assert.Equal(["*.bak", "*.tmp"], loaded.ParsedExcludePatterns);
    }

    [Fact]
    public void CompressionDefaults_FallbackMatchesTheOldBehaviour()
    {
        // 没有设置文件时必须等于引入这一页之前的行为，否则"没动过设置"的用户会被悄悄改口味。
        var loaded = CompressionDefaults.Load(new AppSettings());

        Assert.Equal(CompressionDefaults.Fallback, loaded);
        Assert.Equal(CompressionLevelOption.Normal, loaded.Level);
        Assert.False(loaded.IncludeRootFolder);
        Assert.True(loaded.ExcludeThumbsDb);
        Assert.Empty(loaded.ParsedExcludePatterns);
        Assert.False(loaded.KeepProgressWindow);
        Assert.Equal(SettingKeys.OutputFolderMode.LastUsed, loaded.OutputMode);
    }

    [Fact]
    public void CompressionDefaults_UnknownLevelIdInSettingsFallsBackToNormal()
    {
        var settings = new AppSettings();
        settings.SetInt(SettingKeys.CompressLevel, 4711);

        Assert.Equal(CompressionLevelOption.Normal, CompressionDefaults.Load(settings).Level);
    }

    [Fact]
    public void CompressionDefaults_ClearingAFieldRemovesItsKey()
    {
        var settings = new AppSettings();
        var on = new CompressionDefaults(
            CompressionLevelOption.Normal, true, true, "*.bak", false, false,
            SettingKeys.OutputFolderMode.Fixed, @"D:\drops");
        on.Save(settings);

        var off = on with { OutputMode = SettingKeys.OutputFolderMode.LastUsed, FixedOutputFolder = null };
        off.Save(settings);

        Assert.False(settings.Has(SettingKeys.CompressOutputFolder));

        // 纯空白等同于没填：设置文件里不该留一条 "compress_exclude_patterns = "。
        off.Save(settings);
        var blank = off with { ExcludePatternText = "   " };
        blank.Save(settings);
        Assert.False(settings.Has(SettingKeys.CompressExcludePatterns));
    }
}
