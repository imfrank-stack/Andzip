using Andzip.Core.Archive;
using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 「设置 → 压缩」与「设置 → 解压」的默认值，以及压缩时的排除模式。
/// <para>
/// 设置页本身是 GUI，测不了；但它读写的那份状态测得了 —— 尤其是"没有设置文件时必须等于
/// 引入这一页之前的行为"这一条，缺省写反会让每个老用户突然改口味，而且没人会想到是设置页干的。
/// </para>
/// <para>
/// 排除规则另外值得钉：它会<strong>静默少打包里的文件</strong>，
/// 一个把 <c>x.bak.txt</c> 也吃进去的模式，用户要到解压时才发现少东西。
/// </para>
/// </summary>
public class SettingsDefaultsTests
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
            FixedOutputFolder: @"D:\drops",
            // 两项都要真的走到设置文件里再回来，否则界面勾了个寂寞。
            PreallocateDisk: true,
            ShareSources: true);

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
        // 预分配会占住比最终包大很多的磁盘，独占源文件会挡住别的程序 —— 两个都不能替用户打开。
        Assert.False(loaded.PreallocateDisk);
        Assert.False(loaded.ShareSources);
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

    // ---------- ExtractionDefaults（设置 → 解压） ----------

    [Fact]
    public void ExtractionDefaults_RoundTripsThroughSettings()
    {
        var wanted = new ExtractionDefaults(
            SingleFileToFolder: true,
            OpenFolderAfterExtract: false,
            KeepProgressWindow: true,
            DoubleClick: ArchiveDoubleClick.ExtractDirectly);

        var settings = new AppSettings();
        wanted.Save(settings);

        Assert.Equal(wanted, ExtractionDefaults.Load(settings));
    }

    [Fact]
    public void ExtractionDefaults_FallbackMatchesTheOldBehaviour()
    {
        // 同压缩页那条：没动过设置的用户必须得到引入这一页之前的行为。
        var loaded = ExtractionDefaults.Load(new AppSettings());

        Assert.Equal(ExtractionDefaults.Fallback, loaded);
        Assert.False(loaded.SingleFileToFolder);
        Assert.False(loaded.OpenFolderAfterExtract);
        Assert.False(loaded.KeepProgressWindow);
        Assert.Equal(ArchiveDoubleClick.OpenInAndzip, loaded.DoubleClick);
    }

    [Fact]
    public void ExtractionDefaults_DoubleClickSharesTheKeyTheStartupPathReads()
    {
        // 「双击 → 直接解压」真正生效的地方是启动时那次读取（App.TryResolveTask）。
        // 这一页写的是别的键的话，设置就只是界面上好看 —— 所以钉住键名和取值。
        var settings = new AppSettings();
        (ExtractionDefaults.Fallback with { DoubleClick = ArchiveDoubleClick.ExtractDirectly })
            .Save(settings);

        Assert.Equal("extract", settings.GetString(SettingKeys.DoubleClickAction));
        Assert.True(SettingKeys.ShouldExtractDirectly(settings.GetString(SettingKeys.DoubleClickAction)));
    }

    [Fact]
    public void ExtractionDefaults_DoubleClickKeepsTheOldBehaviourOnAnUnreadableValue()
    {
        // 手改过 settings.ini、或来自更新版本的取值：宁可回到"打开主窗口"，
        // 也不要双击就把包解压出去 —— 后者是会往磁盘上写东西的。
        var settings = new AppSettings();
        settings.SetString(SettingKeys.DoubleClickAction, "shrink");

        Assert.Equal(ArchiveDoubleClick.OpenInAndzip, ExtractionDefaults.Load(settings).DoubleClick);
    }

    // ---------- ViewDefaults（设置 → 查看） ----------

    [Fact]
    public void ViewDefaults_FallbackShowsEverything()
    {
        // 这一页之前的界面是"全都在、默认详情列表"。缺省必须一模一样，
        // 否则更新完设置文件不存在（或被删过）的用户会发现工具栏不见了。
        var loaded = ViewDefaults.Load(new AppSettings());

        Assert.Equal(ViewDefaults.Fallback, loaded);
        Assert.True(loaded.ShowStatusBar);
        Assert.False(loaded.IconViewByDefault);

        // 唯一故意偏离"引入这一页之前的界面"的一项：树默认**不**摊开（2026-10-13 改的）。
        // 深包一打开就把整棵目录铺平，第一屏全是文件夹名，看不到内容。
        Assert.False(loaded.AutoExpandTree);
    }

    [Fact]
    public void ViewDefaults_RoundTripsIncludingTheOffCases()
    {
        var wanted = new ViewDefaults(
            ShowStatusBar: false,
            IconViewByDefault: true, AutoExpandTree: false);

        var settings = new AppSettings();
        wanted.Save(settings);

        Assert.Equal(wanted, ViewDefaults.Load(settings));
    }

    [Fact]
    public void ToolBarMode_SurvivesAReLoadIncludingTheOffCases()
    {
        // 4.4.0 之前这一项是 ViewDefaults 上的一个布尔（「设置 → 查看」那个「显示工具栏」开关），
        // 那时"关掉的项最容易写错"这条就是冲它说的：存储用 "0" 而 GetBool 的缺省是 true，
        // 键被误删就会变成"关不掉"。现在它换成三档、归「视图 → 工具栏」管，
        // 同一件事仍然要有人钉：小档与隐藏这两档存进去再读回来必须还是它自己。
        var path = Path.Combine(Path.GetTempPath(), $"andzip-toolbar-{Guid.NewGuid():N}.ini");
        try
        {
            foreach (var mode in new[] { ToolBarMode.Large, ToolBarMode.Small, ToolBarMode.Hidden })
            {
                var settings = new AppSettings();
                settings.SetString(SettingKeys.ViewToolBar, mode.ToStorage());
                settings.Save(path);

                Assert.Equal(mode, SettingKeys.ParseToolBarMode(AppSettings.Load(path).GetString(SettingKeys.ViewToolBar)));
            }
        }
        finally
        {
            File.Delete(path);
        }

        // 认不出来的取值（手改坏了、或来自更新的版本）回到大档，而不是回到"隐藏"。
        Assert.Equal(ToolBarMode.Large, SettingKeys.ParseToolBarMode("banana"));
        Assert.Equal(ToolBarMode.Large, SettingKeys.ParseToolBarMode(null));
    }

    [Fact]
    public void LanguageKey_AbsentMeansNoPreference()
    {
        // 没设置过语言时 LangLoader 要走它自己的回退（SimpChinese），
        // 所以这里必须是 null 而不是空串 —— 空串会被当成一个查不到的语言代码。
        var settings = new AppSettings();

        Assert.Equal(string.Empty, settings.GetString(SettingKeys.Language));
    }
}
