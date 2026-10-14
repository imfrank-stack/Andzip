using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Iconography;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Win32;

namespace Andzip.App.UiTests;

/// <summary>
/// 「设为桌面图标」这一条路的实测：真的生成 .ico、真的改真的 <c>.lnk</c>、真的读写那条注册表值。
/// <para>为什么不用假的文件系统：快捷方式的图标字段是 shell 读的，注册表那条是资源管理器读的 ——
/// 这两处"代码写了"与"屏幕上换了"是两件事，而它们的失败方式都是"用户点了，什么都没发生"。</para>
/// <para>落点全部注入临时目录；注册表只动自己建的那两条探针键（前缀 <c>Andzip.IconProbeOnly.</c>），
/// 用完连键一起删，不碰开发机上真正的文件关联。</para>
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("界面状态")]
public class IconStudioTests : IDisposable
{
    // 作用域自己一个前缀：这台开发机上真留着七条 <c>Andzip.*\DefaultIcon</c>（早年手动关联留下的），
    // 测试要是照着默认前缀跑，就会把用户实际的格式图标改到临时目录里 ——
    // 而「恢复默认」那一步写回去的会是测试主机的 exe 路径。
    private const string Prefix = "Andzip.IconProbeOnly.";
    private const string ProbeProgId = @"Software\Classes\Andzip.IconProbeOnly.zip";
    private const string ProbeOrphan = @"Software\Classes\Andzip.IconProbeOnly.iso";
    private const string FakeExe = @"D:\fake\Andzip.exe";

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-icon-").FullName;
    private readonly string _iconDirectory;
    private readonly string _link;
    private readonly IconPreferences _preferences =
        new(new IconPalette("#FF0000", "#00FF00", "#0000FF", "#111111", "#222222", 0), IconGlyph.Punched, true);

    public IconStudioTests()
    {
        _iconDirectory = Path.Combine(_root, "icon");
        _link = Path.Combine(_root, "Andzip.lnk");

        var exe = Path.Combine(_root, "Andzip.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00]);
        ShellLink.Create(_link, exe, arguments: null, description: "探针");
    }

    public void Dispose()
    {
        Assets.ClearCustomIcon();
        Registry.CurrentUser.DeleteSubKeyTree(ProbeProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ProbeOrphan, throwOnMissingSubKey: false);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private IconStudio Studio() => new(_iconDirectory, Prefix, FakeExe);

    private static void MakeProbeProgId()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ProbeProgId);
        classes!.CreateSubKey("DefaultIcon")!.SetValue(string.Empty, @"C:\old.exe,0");
        Registry.CurrentUser.CreateSubKey(ProbeOrphan);
    }

    [AvaloniaFact]
    [SupportedOSPlatform("windows")]
    public void ApplyingWritesTheIconFilesAndPointsTheShortcutAtThem()
    {
        var outcome = Studio().Apply(_preferences, [_link]);

        Assert.Null(outcome.Failure);
        Assert.True(File.Exists(Studio().IcoPath), "没有生成 .ico —— 快捷方式没东西可指。");
        Assert.True(File.Exists(Studio().PngPath), "没有生成 512 的 PNG。");
        Assert.Equal(1, outcome.ShortcutsUpdated);

        var (path, index) = ShellLink.ReadIcon(_link);
        Assert.Equal(Studio().IcoPath, path);
        Assert.Equal(0, index);

        // 快捷方式换了，正在跑的这份程序也得换 —— 否则"桌面上是新图标、任务栏是旧的"。
        Assert.NotNull(Assets.AppIcon);
    }

    [AvaloniaFact]
    [SupportedOSPlatform("windows")]
    public void RestoringTakesTheFilesAwayAndLetsTheShortcutFollowItsTargetAgain()
    {
        var studio = Studio();
        studio.Apply(_preferences, [_link]);

        var outcome = studio.Restore([_link]);

        Assert.False(File.Exists(studio.IcoPath), "界面上说恢复了，盘上那份 .ico 还在。");
        Assert.False(File.Exists(studio.PngPath), "那份 PNG 也该一起删掉。");
        Assert.Equal(1, outcome.ShortcutsUpdated);

        // 空图标字段 = "跟目标走"，也就是 exe 里内嵌那张。指回 .lnk 自己是另一种写法。
        Assert.Equal(string.Empty, ShellLink.ReadIcon(_link).Path);
        Assert.NotNull(Assets.AppIcon);
    }

    /// <summary>
    /// 绿色版没有快捷方式 —— 这条路必须<strong>照样成功，并且少报一件事</strong>。
    /// <para>症状会是这样的：用户从 <c>dist</c> 里双击绿色版，点了「设为桌面图标」，
    /// 界面上写"桌面图标已更新"，而桌面上本来就没有 Andzip。那是 1.8.4 那句
    /// "桌面与开始菜单的快捷方式已创建"的翻版。</para>
    /// </summary>
    [AvaloniaFact]
    [SupportedOSPlatform("windows")]
    public void ApplyingWithoutAnyShortcutStillSucceedsAndSaysSo()
    {
        var outcome = Studio().Apply(_preferences, [Path.Combine(_root, "根本没有这一项.lnk")]);

        Assert.Null(outcome.Failure);
        Assert.True(File.Exists(Studio().IcoPath));
        Assert.Equal(0, outcome.ShortcutsUpdated);
        Assert.False(outcome.ShortcutsBlocked);
    }

    [AvaloniaFact]
    [SupportedOSPlatform("windows")]
    public void OnlyProgIdsThatAlreadyHaveAnIconAreTouched()
    {
        MakeProbeProgId();

        var studio = Studio();
        var outcome = studio.Apply(_preferences, []);

        Assert.Equal(1, outcome.ProgIdsUpdated);
        using (var icon = Registry.CurrentUser.OpenSubKey($@"{ProbeProgId}\DefaultIcon"))
        {
            Assert.Equal(studio.IcoPath, icon?.GetValue(string.Empty));
        }

        // 另一条没有 DefaultIcon 的 ProgId：换图标这件事不该顺手给它建一个出来。
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{ProbeOrphan}\DefaultIcon"));

        studio.Restore([]);
        using var back = Registry.CurrentUser.OpenSubKey($@"{ProbeProgId}\DefaultIcon");
        Assert.Equal($@"{FakeExe},0", back?.GetValue(string.Empty));
    }

    /// <summary>启动时那份 .ico 不在（换机器 / 被清理），要安静地退回内嵌那一张而不是抛。</summary>
    [AvaloniaFact]
    [SupportedOSPlatform("windows")]
    public void AMissingIconFileOnStartupFallsBackToTheBuiltInOne()
    {
        Assert.False(Studio().RestoreFromSettings(_preferences));
        Assert.False(Studio().RestoreFromSettings(_preferences with { Applied = false }), "没设过就不该去读那个文件");

        Studio().Apply(_preferences, []);
        Assert.True(Studio().RestoreFromSettings(_preferences));
    }

    /// <summary>
    /// 删不掉那份 .ico 时，「恢复默认」要<strong>说败</strong>，不许写"已恢复出厂那张图标"。
    /// <para>写死的回话会在它描述的事第一次出错那天变成假话 —— 这一档项目里已经栽过两次
    /// （1.8.4 的"桌面快捷方式已创建"、2.0.0 的"已经装过"）。</para>
    /// </summary>
    [AvaloniaFact]
    public void ARestoreThatCannotDeleteTheFileSaysSo()
    {
        var studio = Studio();
        studio.Apply(_preferences, [_link]);

        File.SetAttributes(studio.IcoPath, FileAttributes.ReadOnly);

        var outcome = studio.Restore([_link]);

        Assert.NotNull(outcome.Failure);
        Assert.True(File.Exists(studio.IcoPath), "报告里说没成，文件倒是没了（或反过来）。");

        File.SetAttributes(studio.IcoPath, FileAttributes.Normal);
    }

    // ------------------------------------------------------------- 界面上那一节

    [AvaloniaFact]
    public void TheViewPageCarriesAnIconStudioWithBothPreviewsDrawn()
    {
        var lang = TestLang.Chinese();
        var path = SettingsPath();
        var window = new SettingsWindow(lang, new AppSettings(), path).Shown();

        Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_VIEW")));

        var shown = VisualQuery.VisibleTexts(window);
        Assert.Contains(lang.Label("TEXT_ICON_TITLE"), shown);
        Assert.Contains(lang.Label("TEXT_ICON_APPLY"), shown);
        Assert.Contains(lang.Label("TEXT_ICON_RESTORE"), shown);
        Assert.Contains(lang.Label("TEXT_ICON_DERIVE"), shown);

        // 八套预设、四档方向、两种字形 —— 少一个就是那一排没建出来（而不是"用户没看见"）。
        Assert.Equal(IconPalette.Presets.Length, VisualQuery.Of<Button>(window)
            .Count(b => (Avalonia.Automation.AutomationProperties.GetName(b) ?? string.Empty).StartsWith('#')));
        Assert.Equal(4, VisualQuery.Of<Button>(window).Count(b => Arrows().Contains(VisualQuery.TextOf(b))));
        // 两种字形各一张预览。标题栏那枚也是 Image，所以要按预览的尺寸挑出来数。
        Assert.Equal(2, VisualQuery.Of<Image>(window).Count(i => i.Source is not null && i.Width == 64));

        // 五格颜色都带着合法的十六进制初值。
        var hexes = VisualQuery.Of<TextBox>(window).Select(t => t.Text).Where(t => t is not null && t.Length == 7).ToList();
        Assert.Equal(5, hexes.Count);
        Assert.All(hexes, hex => Assert.NotNull(IconPalette.Normalize(hex!)));
    }

    /// <summary>
    /// 这一节比窗口还高，所以<strong>必须有一个能滚的宿主装着</strong>。
    /// <para>真机核对抓到的形状：「图标配色」的标题与一排预设色片露在外面，
    /// 下面两格预览、五格颜色与那颗「恢复默认」全在窗口底下 —— 而这一页没有滚动条，
    /// 于是那半截功能等于不存在（与 1.9.2 那次"写死 168 把内容切掉一半"同一族）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheIconSectionIsReachableByScrollingInsteadOfBeingCutOff()
    {
        var lang = TestLang.Chinese();
        var window = new SettingsWindow(lang, new AppSettings(), SettingsPath()).Shown();

        Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_VIEW")));
        UiWait.Pump(120);

        var restore = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_ICON_RESTORE")));

        // 它确实落在窗口底下：不滚就点不到，这条断言才有意义。
        var bottom = restore.TranslatePoint(new Avalonia.Point(0, restore.Bounds.Height), window);
        Assert.NotNull(bottom);
        Assert.True(bottom!.Value.Y > window.Bounds.Height,
            $"「恢复默认」的底在 y={bottom.Value.Y:0}，窗口才 {window.Bounds.Height:0} —— 这条用例的前提没了（内容变矮了？）");

        var scroller = Ancestor<Avalonia.Controls.ScrollViewer>(restore);
        Assert.NotNull(scroller);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroller!.VerticalScrollBarVisibility);
    }

    private static T? Ancestor<T>(Control control) where T : class
    {
        for (Control? at = control.Parent as Control; at is not null; at = at.Parent as Control)
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }

    [AvaloniaFact]
    public void PickingAPresetWritesTheColoursIntoTheSettingsFile()
    {
        var lang = TestLang.Chinese();
        var path = SettingsPath();
        var window = new SettingsWindow(lang, new AppSettings(), path).Shown();

        Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_VIEW")));

        var second = IconPalette.Presets[1];
        var chip = VisualQuery.Of<Button>(window)
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b)
                == $"{second.Front0} {second.Front1} {second.Front2}");

        Clicker.Invoke(chip);

        var saved = IconPreferences.Load(AppSettings.Load(path));
        Assert.Equal(second.Front0, saved.Palette.Front0);
        Assert.Equal(second.Front2, saved.Palette.Front2);

        // 色片只管颜色，方向是另一件事：换预设不许顺手把用户挑过的方向改了。
        Assert.Equal(IconPalette.Default.Direction, saved.Palette.Direction);
    }

    /// <summary>
    /// 一格打错的十六进制<strong>不许</strong>把图标改成黑色。
    /// <para>认不得就保留原值并把那格照原样显示回去 —— 一个静默的"当黑色处理"会让用户
    /// 看到图标突然变黑，然后开始怀疑这个软件。</para>
    /// </summary>
    [AvaloniaFact]
    public void AnUnparseableHexKeepsTheStoredColour()
    {
        var lang = TestLang.Chinese();
        var path = SettingsPath();
        var window = new SettingsWindow(lang, new AppSettings(), path).Shown();

        Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_VIEW")));

        var box = VisualQuery.Of<TextBox>(window).First(t => t.Text == IconPalette.Default.Front0);
        box.Text = "zzzzzz";
        box.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = TextBox.KeyDownEvent,
            Key = Avalonia.Input.Key.Enter,
        });

        var saved = IconPreferences.Load(AppSettings.Load(path));
        Assert.Equal(IconPalette.Default.Front0, saved.Palette.Front0);
        Assert.Equal(IconPalette.Default.Front0, box.Text);
    }

    private string SettingsPath() => Path.Combine(_root, $"settings-{Guid.NewGuid():N}.ini");

    private static IEnumerable<string> Arrows() => ["↘", "↓", "→", "↙"];

    private static Button FindButton(Window window, string text)
        => VisualQuery.Of<Button>(window).Single(b => Equals(VisualQuery.TextOf(b), text));
}
