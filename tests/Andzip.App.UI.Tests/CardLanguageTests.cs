using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using System.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 1.7.0 那套"灰画布 + 白色圆角阴影卡片"的守门用例。
/// <para>规范 §二 从前写着"SimpleTheme 用不了阴影，层级只能靠底色差"。这句话现在只对了一半：
/// 模板控件（Button / TextBox / CheckBox）确实不吃 <c>BoxShadow</c>，
/// 但卡片是我们自己搭的 <see cref="Border"/>，它有这个属性而且<strong>真的画得出来</strong>。
/// 这几条钉的就是"新语言确实用上了"，而不是"某个属性被赋过值"。</para>
/// </summary>
[Collection("界面状态")]
public class CardLanguageTests
{
    /// <summary>密码恢复那一扇只要三个回调就能开；这里量的是它的卡片，不是它的逻辑。</summary>
    private static PasswordRecoveryWindow RecoveryDialog(Andzip.Core.Lang.LangCatalog lang) => new(
        lang, null, () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), (_, _) => { });

    /// <summary>查找窗口要一份清单才开得起来；这里只关心它长什么样，给一条成员就够。</summary>
    private static ArchiveListing FindListing() => new(
        @"C:\somewhere\box.zip",
        [new ArchiveEntryInfo("a.txt", "a.txt", false, 1, 1, false, "Store", new DateTime(2026, 3, 4, 5, 6, 0))],
        1, 0, 1, 1, null);
    /// <summary>
    /// 提示框里的长文案必须<strong>折行</strong>，不能被窗口右边界裁掉。
    /// <para>1.8.6 真机截图抓到的：修复那条"这个包的骨架是完整的：…都对得上，没" —— 后面半句
    /// 在屏幕外。文案设了 <c>TextWrapping.Wrap</c> 却仍不折行，是因为它当时装在横向
    /// <c>StackPanel</c> 里：那个面板给子元素的是无限宽，Wrap 于是永远不触发。
    /// 一条说不完的错误信息被裁成半句，等于没有那条信息。</para>
    /// </summary>
    [AvaloniaFact]
    public void ALongMessageWrapsInsteadOfRunningOffTheEdge()
    {
        var lang = TestLang.Chinese();
        var message = lang.Label("TEXT_REPAIR_HEALTHY");
        var window = new MessageWindow(lang, "修复压缩包", message).Shown();

        var text = VisualQuery.Of<TextBlock>(window).Single(t => Equals(t.Text, message));

        Assert.True(text.Bounds.Width < window.Bounds.Width,
            $"正文量到的宽度是 {text.Bounds.Width}，窗口才 {window.Bounds.Width} —— 说明它按无限宽测量，"
            + "Wrap 根本没生效，超出部分在屏幕外");
        Assert.True(text.Bounds.Height > Design.Type.BodyLineHeight * 1.5,
            $"正文只占 {text.Bounds.Height} 高，是一行没折的长句（一行读不完就被裁掉）");
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]   // 安装器也是这张清单里的一员，而它只在 Windows 上存在
    public void EveryDialogWrapsItsContentInAnElevatedCard()
    {
        var lang = TestLang.Chinese();
        var archive = @"C:\somewhere\box.zip";
        var windows = new Window[]
        {
            new MessageWindow(lang, "标题", "正文"),
            new NewArchiveWindow(lang),
            new PasswordWindow(lang),
            new ExtractPathWindow(lang, archive),
            new DuplicateFileWindow(lang, ["a.txt"]),
            new VolumeSizeWindow(lang, 100),
            new RepairWindow(lang, archive, @"C:\somewhere\box.修复.zip"),
            new ProgressWindow(lang, new CancellationTokenSource()),

            // 「重命名」那一格也是同一族：它长得像对话框，就得守对话框的规矩
            new TextPromptWindow(lang, "MENUSTR_RENAME_FILE", "TEXT_LABEL_RENAME_ENTRY", initial: "a.txt"),

            // 查找那一扇是这张清单里唯一**非模态**的 —— 卡片那一套不该因为"它不挡主窗口"就少一层
            new FindWindow(lang, FindListing()),

            // 关于那一屏也是同一族：它不因为"只是展示信息"就少一层卡片
            new AboutWindow(lang),

            // 密码恢复那一扇也是：它长得像对话框，就得守对话框的规矩
            RecoveryDialog(lang),
            // 安装器也是同一个界面语言里的一员：它不该长得像另一个程序
            new InstallerWindow(lang),
        };

        foreach (var window in windows)
        {
            Assert.True(window.Content is Border,
                $"{window.GetType().Name} 的内容没有包在卡片里（规范 5.6）");
            var card = (Border)window.Content!;

            Assert.True(card.BoxShadow.Count > 0, $"{window.GetType().Name} 那张卡没有阴影");
            Assert.True(card.CornerRadius.TopLeft >= 8,
                $"{window.GetType().Name} 的卡片圆角只有 {card.CornerRadius.TopLeft}");
        }
    }

    /// <summary>
    /// 每一张无边框窗口，圆角外面那一圈必须是<strong>透明的</strong>（2.4.1）。
    /// <para>用户对着安装与卸载向导截的图：圆角面板外面是一整块方方的纯白，蓝色桌面被挡在它外面 ——
    /// 那层阴影等于画在白纸上，"浮起来"这件事整个丢了。量给开发记录第七节第 55 条：
    /// 同一列上桌面蓝 <c>#01A0F4</c> 到 y=38 直接跳成 <c>#FFFFFF</c>，中间没有影子过渡；
    /// 影子是在白里衰减的（<c>#FDFDFD → #E2E5E7</c>），不是垫在桌面上。</para>
    /// <para>为什么只有这两扇窗这样：底色写在 <c>WindowSurface()</c> 里，而"每个窗口构造时调一次"
    /// 靠的是自觉 —— 安装器与提示框谁都没调，属性读回来是主题那支白，屏幕上就是一块白。
    /// 现在这件事归 <c>Chromeless()</c> 管，所以这条量的是<strong>每一扇</strong>无边框窗口，
    /// 而不是"安装器有没有补上那一句" —— 补那一句只能保证这一扇，漏的那两扇恰恰证明"记得调"这条规矩靠不住。</para>
    /// <para>量 alpha 不量颜色：白底与透明在一张截图上都可能是 <c>#FFFFFF</c>，
    /// 只有 alpha 分得开"窗口没画"与"窗口画了白"。</para>
    /// </summary>
    [AvaloniaFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TheRingOutsideThePanelIsTransparentOnEveryWindow()
    {
        Design.Use(dark: false);
        var lang = TestLang.Chinese();
        var archive = @"C:\somewhere\box.zip";
        var settingsPath = Path.Combine(Path.GetTempPath(), $"andzip-ring-{Guid.NewGuid():N}.ini");

        var windows = new (string Where, Window Window)[]
        {
            ("主窗口", new MainWindow(lang, new AppSettings())),
            ("安装向导", new InstallerWindow(lang)),
            ("提示框", new MessageWindow(lang, "标题", "正文")),
            ("新建压缩框", new NewArchiveWindow(lang)),
            ("口令框", new PasswordWindow(lang)),
            ("解压路径框", new ExtractPathWindow(lang, archive)),
            ("重名询问框", new DuplicateFileWindow(lang, ["a.txt"])),
            ("分卷大小框", new VolumeSizeWindow(lang, 100)),
            ("修复框", new RepairWindow(lang, archive, @"C:\somewhere\box.修复.zip")),
            ("进度框", new ProgressWindow(lang, new CancellationTokenSource())),
            ("查找窗口", new FindWindow(lang, FindListing())),
            ("关于窗口", new AboutWindow(lang)),
            ("密码恢复窗口", RecoveryDialog(lang)),
            ("重命名框", new TextPromptWindow(lang, "MENUSTR_RENAME_FILE", "TEXT_LABEL_RENAME_ENTRY", initial: "a.txt")),
            ("设置窗口", new SettingsWindow(lang, new AppSettings(), settingsPath)),
        };

        try
        {
            foreach (var (where, window) in windows)
            {
                window.Shown();
                UiWait.Pump(50);

                var alpha = Pixels.AlphaAt(window, new Point(0, window.Bounds.Height / 2));
                var far = Pixels.AlphaAt(window, new Point(1, window.Bounds.Height / 2));

                window.Close();
                UiWait.Pump(20);

                Assert.True(alpha == 0 && far == 0,
                    $"{where}最外一圈 alpha={alpha}/{far} —— 圆角外面不是透明的，是窗口自己那块底色，"
                    + "屏幕上就是一圈方方的白板把阴影垫掉（规范 5.1）");
            }
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    /// <summary>
    /// 整块窗口就是一张圆角阴影面板，顶部不再是蓝带、也不再是"外壳卡"。
    /// <para>断言的是四件互相独立的事：窗口自己透明（圆角外要能看见桌面）、
    /// 客户端区外扩且系统按钮关掉（否则标题栏会是系统那条方的）、
    /// 根上那层是圆角 + 有阴影的 Border、顶部那块表面是面板色而不是品牌蓝。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheWholeWindowIsOneRoundedPanelWithNoBlueBand()
    {
        Design.Use(dark: false);
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        Assert.True(window.Background is ISolidColorBrush clear && clear.Color.A == 0,
            "窗口自己不是透明的 —— 圆角外会留下四个方方的角");
        Assert.Contains(WindowTransparencyLevel.Transparent, window.TransparencyLevelHint);
        Assert.True(window.ExtendClientAreaToDecorationsHint, "客户端区没外扩，系统标题栏还在");
        Assert.Equal(Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome, window.ExtendClientAreaChromeHints);

        var panel = Assert.IsType<Border>(window.Content);
        Assert.True(panel.CornerRadius.TopLeft >= 12, $"面板圆角只有 {panel.CornerRadius.TopLeft}");
        Assert.True(panel.BoxShadow.Count > 0, "面板没有阴影，贴在任何背景上都分不出来");

        var header = Pixels.At(window, new Point(window.Bounds.Width / 2, 60));
        Assert.False(Pixels.Same(header, BrandColor), "顶部又变回实心蓝带了");
        Assert.True(Pixels.Same(header, Pixels.Of(Design.Surface.Base)),
            $"顶部不是面板色而是 {Pixels.Name(header)}");
    }

    /// <summary>
    /// 阴影必须在窗口边界之内衰减干净 —— 否则屏幕上是一条齐刷刷的切痕。
    /// <para>1.8.0 那版被用户否掉的"不自然"就是这个：阴影的弥散层能伸到 13 DIP 外，
    /// 而透明边距只有 12 DIP，窗口边界把影子切了一刀，四个角上最明显
    /// （角上影子本来就走斜线，切痕离面板更近）。</para>
    /// <para>量的是 alpha：面板之外本来全透明，影子每占一格那一格就不再是 0。
    /// 所以"紧贴面板外沿 alpha&gt;0"证明影子在，"窗口最外一圈 alpha==0"证明没被切。</para>
    /// </summary>
    [AvaloniaFact]
    public void PanelShadowDecaysInsideTheWindowInsteadOfBeingCutOff()
    {
        Design.Use(dark: false);
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var panel = (Border)window.Content;
        var edge = panel.TranslatePoint(new Point(0, panel.Bounds.Height / 2), window)!.Value;

        // 面板左缘外 2px：应当有影子（alpha 不为 0）
        var near = Pixels.AlphaAt(window, new Point(edge.X - 2, edge.Y));
        Assert.True(near > 0, "面板外沿 2px 全透明 —— 根本没有阴影");

        // 窗口最外一圈四条边：必须已经衰减干净，否则就是被切了一刀
        var w = window.Bounds.Width;
        var h = window.Bounds.Height;
        var probes = new (string Where, Point At)[]
        {
            ("左", new Point(0, h / 2)),
            ("右", new Point(w - 1, h / 2)),
            ("上", new Point(w / 2, 0)),
            ("下", new Point(w / 2, h - 1)),
            ("左下角", new Point(1, h - 1)),
            ("右下角", new Point(w - 2, h - 1)),
        };

        foreach (var (where, at) in probes)
        {
            var alpha = Pixels.AlphaAt(window, at);
            Assert.True(alpha == 0,
                $"{where}那一圈 alpha={alpha}：阴影被窗口边界切断了（{nameof(Design.Float)}.{nameof(Design.Float.Margin)} 装不下 {nameof(Design.Shadow)}.Floating）");
        }
    }

    /// <summary>
    /// 深色下标题栏跟着变深 —— 这件事 1.5.0 做不到（DWM 属性返回 S_OK 但屏幕不动，
    /// 见开发记录第七节第 28 条），1.7.0 因为标题栏是我们自己画的，自然就成了。
    /// </summary>
    [AvaloniaFact]
    public void DarkModeNowReachesTheTitleBar()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var at = new Point(window.Bounds.Width / 2, 60);
        var light = Pixels.At(window, at);

        Design.Use(dark: true);
        UiWait.Pump(100);
        var dark = Pixels.At(window, at);

        try
        {
            Assert.False(Pixels.Same(light, dark), "同一个点深浅一致 —— 标题栏没跟着调色板走");
            Assert.True(Pixels.Luminance(dark) < 0.2,
                $"深色下面板还是 {Pixels.Name(dark)}，没暗下去");
        }
        finally
        {
            Design.Use(dark: false);
        }
    }

    /// <summary>最大化时圆角与透明边距要收掉，否则四周会露出一圈桌面。</summary>
    [AvaloniaFact]
    public void MaximizingDropsTheCornerRadius()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);
        var panel = (Border)window.Content!;
        var rest = (panel.CornerRadius.TopLeft, panel.Margin.Left);

        window.WindowState = WindowState.Maximized;
        UiWait.Pump(50);
        var maximized = (panel.CornerRadius.TopLeft, panel.Margin.Left);

        Assert.True(rest.Item1 >= 12, $"正常状态就该有圆角，实测 {rest.Item1}");
        Assert.True(maximized.Item1 < rest.Item1,
            $"最大化后圆角没收掉（{rest.Item1} → {maximized.Item1}），四个角会露出桌面");
        Assert.True(maximized.Item2 < rest.Item2,
            $"最大化后透明边距没收掉（{rest.Item2} → {maximized.Item2}），窗口会小一圈");

        window.WindowState = WindowState.Normal;
        UiWait.Pump(50);
        Assert.Equal(rest, (panel.CornerRadius.TopLeft, panel.Margin.Left));
    }

    [AvaloniaFact]
    public void SettingsPagesUseSwitchesNotCheckBoxes()
    {
        var lang = TestLang.Chinese();
        var path = Path.Combine(Path.GetTempPath(), $"andzip-card-{Guid.NewGuid():N}.ini");

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), path).Shown();
            Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_COMPRESS")));
            UiWait.Pump(50);

            // 设置页的布尔项换成开关（规范 5.9）：这一页上不该再有勾选框。
            Assert.NotEmpty(VisualQuery.Of<SettingToggle>(window));
            Assert.Empty(VisualQuery.Of<CheckBox>(window));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 文件关联那一页仍然必须是勾选框 —— 它是"这一批里选哪几个"的多选清单，
    /// 不是"某个行为开不开"。这条与上一条成对：防止开关被当成新皮肤到处推。
    /// </summary>
    [AvaloniaFact]
    public void AssociationPageKeepsCheckBoxesBecauseItIsAList()
    {
        var lang = TestLang.Chinese();
        var path = Path.Combine(Path.GetTempPath(), $"andzip-card-{Guid.NewGuid():N}.ini");

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), path).Shown();
            Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_FILE_ASSOCIATION")));
            UiWait.Pump(50);

            Assert.NotEmpty(VisualQuery.Of<CheckBox>(window));
            Assert.Empty(VisualQuery.Of<SettingToggle>(window));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 换主题时工具栏必须<strong>整条重做</strong>，不能只刷卡片。
    /// <para>这条钉的是一个真实出现过的 bug：工具栏改成浅色卡片之后，按钮的文字色从"永远白色"
    /// 变成了"跟着调色板走"，而 <c>ApplyTheme</c> 当时只重画了卡片底色 ——
    /// 于是深色下"打开 / 解压 / 新建"三个字变成深灰压深灰，直接消失，
    /// 而禁用项（添加 / 删除）反而还在。截图上看着像"深色版少了三个按钮"。</para>
    /// </summary>
    [AvaloniaFact]
    public void ToolBarLabelsFollowThePaletteNotTheMomentTheyWereBuilt()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();

        Design.Use(dark: true);
        UiWait.Pump(100);

        try
        {
            var label = VisualQuery.Of<TextBlock>(window)
                .First(t => Equals(t.Text, lang.Get("TEXT_TOOLBAR_OPEN")));

            Assert.True(Pixels.Name(label.Foreground) == Pixels.Name(Design.Ink.Primary),
                "深色下工具栏文字还是浅色那份调色板的颜色（"
                + $"{Pixels.Name(label.Foreground)} 应为 {Pixels.Name(Design.Ink.Primary)}）—— 工具栏没跟着重做");
        }
        finally
        {
            Design.Use(dark: false);
        }
    }

    private static readonly Color BrandColor = Color.FromRgb(0x0F, 0x6C, 0xBD);

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Button FindButton(Window window, string text)
        => VisualQuery.Of<Button>(window).Single(b => Equals(VisualQuery.TextOf(b), text));
}
