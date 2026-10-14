using System.Text;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 界面规范（<c>docs/界面设计规范.md</c>）里那两条"禁止"的守门用例。
/// <para>规则写在文档里只会在"恰好想起来"的那次生效 —— 版本号规则就是这么失守过一次
/// （<c>0.13.0</c>，见开发记录第七节第 25 条）。所以能机器检查的都不留给自觉。</para>
/// </summary>
[Collection("界面状态")]
public class DesignTokenTests
{
    /// <summary>
    /// 视图代码里不许再出现字面色值：颜色只能来自 <c>Design.cs</c>。
    /// <para>散落的 <c>Color.Parse("#...")</c> 是这次改版的主要债务来源 —— 23 个色值分布在
    /// 10 个文件里，改一处配色就要全仓搜替换。</para>
    /// </summary>
    [Fact]
    public void NoHardcodedColorsOutsideTheDesignTokens()
    {
        var offenders = OffendingLines("Color.Parse", "Brushes.White", "Brushes.Black");

        Assert.True(offenders.Count == 0, "界面里出现了字面色值，规范说颜色只能来自 Design.cs：\n    "
            + string.Join("\n    ", offenders));
    }

    /// <summary>
    /// 颜色不许冻进 <c>static readonly</c> 字段。
    /// <para>1.7.0 截图时发现的：设置页的分组标题在深色下变成一片发灰的浅色字 —— 因为
    /// <c>private static readonly IBrush PageText = Design.Ink.Primary;</c> 在类型第一次被碰时
    /// 就把当时那套调色板的画刷抄死了，之后切换主题它不会跟着变。
    /// 属性（<c>static IBrush PageText =&gt; Design.Ink.Primary;</c>）才是对的：每次读都现取。</para>
    /// <para>这条与"禁止裸色值"是同一族的守门：规则只写在文档里，就会有下一个字段冻在那里。</para>
    /// </summary>
    [Fact]
    public void BrushesMustNotBeFrozenIntoStaticFields()
    {
        var offenders = OffendingLines("static readonly IBrush", "static readonly SolidColorBrush");

        Assert.True(offenders.Count == 0,
            "颜色被抄死在静态字段里了，切主题时它不会跟着变 —— 改成表达式体属性（=> Design.……）：\n    "
            + string.Join("\n    ", offenders));
    }

    /// <summary>
    /// 工具栏按钮的悬停态必须<strong>真的看得见</strong>。
    /// <para>这是改版前最大的缺口：蓝色带上的按钮悬停时一个像素都不变，用户只能靠点下去
    /// 才知道这里能不能点。规范第二条原则就是"每个可点的元素都要有反馈"。</para>
    /// <para>断言方式是量像素：把真指针移到按钮上，同一个点的颜色必须变。</para>
    /// <para><strong>方向随 1.7.0 翻了</strong>：带子从实心蓝改成白色卡片之后，"叠半透明白提亮"
    /// 在白底上等于没有反馈，所以悬停改成往下压一档。规则本身没变（必须看得见），
    /// 变的只是它长什么样 —— 这条断言因此仍然要求一个明确方向，不能退化成"变了就行"。</para>
    /// </summary>
    [AvaloniaFact]
    public void ToolBarButtonsShowAHoverState()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();

        // 换主题时工具栏是整条重做的，重做之后第一帧的偏移量还没刷新；
        // 不等这一帧，TranslatePoint 会把第一个按钮算到菜单条那一行上去（实测 y 差 31）。
        UiWait.Pump(50);

        var button = VisualQuery.Of<Button>(window).First(b => b.Content is Avalonia.Controls.StackPanel);
        var probe = button.TranslatePoint(new Point(6, 6), window)!.Value;

        var rest = Sample(window, probe);
        window.MouseMove(probe);
        Dispatcher.UIThread.RunJobs();
        var hovered = Sample(window, probe);

        Assert.False(Same(rest, hovered), $"悬停时按钮底色没变（前后都是 {Hex(rest)}）：交互态丢了。");
        Assert.True(Luminance(hovered) < Luminance(rest),
            $"悬停在浅色卡片上应当「压深一档」（{Hex(rest)} → {Hex(hovered)}），现在是提亮，读不出反馈。");
    }

    /// <summary>对话框主按钮是实心品牌蓝 —— 主/次按钮的分量差靠这个建立。</summary>
    [AvaloniaFact]
    public void DialogPrimaryButtonIsFilledWithTheBrandColor()
    {
        var lang = TestLang.Chinese();
        var dialog = new MessageWindow(lang, "标题", "正文", "TEXT_BTN_OK", "TEXT_BTN_CANCEL").Shown();

        var ok = VisualQuery.Of<Button>(dialog).Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_BTN_OK")));
        var at = (ok.TranslatePoint(new Point(4, 4), dialog) ?? default);

        Assert.True(Same(Sample(dialog, at), BrandColor),
            $"主按钮底色不是品牌蓝（实测 {Hex(Sample(dialog, at))}）。");
    }

    private static readonly Color BrandColor = Color.FromRgb(0x0F, 0x6C, 0xBD);

    /// <summary>
    /// 每个 <c>new ProgressWindow(…)</c> 之后必须有一句真的把它摆上屏幕。
    /// <para>1.8.6 修的两条是同一个形状：<c>TestArchiveAsync</c> 与修复那条都 new 了进度窗口、
    /// 把 sink 交给后台、跑完再 <c>Close()</c> —— 可那个窗口从头到尾没上过屏。
    /// 于是用户看到的是"点了没反应，过几秒突然弹个结果框"，正是这次的反馈。</para>
    /// <para>行为测试拦不住它：<c>ProgressWindowThreadingTests</c> 自己 new 自己 <c>.Shown()</c>，
    /// 量的是那个机制，不是这条命令。所以这里扫源码 —— 规则只写在文档里就会失守一次（版本号那条）。</para>
    /// </summary>
    [Fact]
    public void EveryProgressWindowIsActuallyShown()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Andzip.App");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("new ProgressWindow(", StringComparison.Ordinal))
                {
                    continue;
                }

                // 局部变量一律叫 progress，所以往后找一句"被显示出来"就够，不必解析控制流。
                var shown = lines.Skip(i).Take(24).Any(l =>
                    l.Contains(".ShowDialog(", StringComparison.Ordinal) || l.Contains(".Show()", StringComparison.Ordinal));

                if (!shown)
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "有进度窗口 new 出来却没人把它摆上屏幕（用户只会看到「点了没反应」）：\n    "
            + string.Join("\n    ", offenders));
    }

    /// <summary>
    /// <summary>
    /// 每一颗按钮与下拉框都要把主题那份 <c>:pressed</c> 挡在面皮外（<c>Chrome.PinFace</c>）。
    /// <para>为什么必须一个个接线点接：<strong>主题按下的那块深灰涂在模板子元素
    /// <c>PART_ContentPresenter</c> 上，应用层样式压不过它，只有给那枚子元素写局部值才压得过</strong>
    /// （规范 §二那条实测）。所以这件事没有"装一次全局就好"的写法 ——
    /// 而漏一处的代价就是一次点击：用户 1.8.8 的截图正是「打开」按下去整块 <c>#686868</c>，
    /// 并且说"好多地方"都一样。</para>
    /// <para>为什么扫源码而不是运行时遍历窗口：<c>ChromeCapabilityTests</c> 那条像素用例已经能在
    /// headless 里量到那块灰（合成按住真的会触发伪类），但它只能盯住它当场构造出来的那颗控件;
    /// 设置窗口那九个「应用」按钮、表头、快捷方式行……都要构造一遍既不划算也不好维护。
    /// 扫源码证明的是"没有漏接"，两者加起来才把这类问题封住。</para>
    /// <para>查 <c>Button</c> / <c>ToggleButton</c> / <c>ComboBox</c>：<c>CheckBox</c> 的量测显示主题的
    /// 伪类不碰它。<c>ComboBox</c> 的灰又分两层 —— 字段那层面皮，和右边那格箭头（模板里内嵌的
    /// 一枚 <c>ToggleButton</c>）自己的面皮，<c>Picker</c> 走的 <c>PinFace</c> 两层都钉。</para>
    /// </summary>
    [Fact]
    public void EveryButtonPinsItsTemplateFace()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Andzip.App");
        string[] pinned = [".Ghost(", ".Accent(", ".Danger(", ".Lift(", ".ToggleSwitch(", ".PinFace(", ".Picker("];
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Chrome.cs"
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var creates = Enumerable.Range(0, lines.Length)
                .Where(i => lines[i].Contains("new Button", StringComparison.Ordinal)
                            || lines[i].Contains("new ToggleButton", StringComparison.Ordinal)
                            || lines[i].Contains("new ComboBox", StringComparison.Ordinal))
                .ToList();

            for (var c = 0; c < creates.Count; c++)
            {
                var i = creates[c];

                // 写法一：接法就写在这一颗自己的范围内 —— 到下一颗建控件那一行为止
                // （再远就会把邻居的接法算到它头上），且不超过 24 行。
                var stop = Math.Min(c + 1 < creates.Count ? creates[c + 1] : lines.Length, i + 24);
                if (lines.Skip(i).Take(stop - i).Any(l => pinned.Any(p => l.Contains(p, StringComparison.Ordinal))))
                {
                    continue;
                }

                // 写法二：先建控件、在别处才包药丸 —— 视图栏那一列就是这种（按钮交出去给外面包）。
                var name = System.Text.RegularExpressions.Regex.Match(lines[i], @"(\w+)\s*=\s*new (Button|ToggleButton)");
                if (name.Success && lines.Any(l => l.Contains($"{name.Groups[1].Value}.", StringComparison.Ordinal)
                        && pinned.Any(p => l.Contains(p, StringComparison.Ordinal))))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "这些控件建出来却没人钉面皮，一按就整块发黑（Chrome.PinFace / Ghost / Accent / Lift）：\n    "
            + string.Join("\n    ", offenders));
    }

    private static List<string> OffendingLines(params string[] needles)
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Andzip.App");
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Design.cs"
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (needles.Any(n => lines[i].Contains(n, StringComparison.Ordinal)))
                {
                    found.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        return found;
    }

    private static string RepositoryRoot()
    {
        // 要的是仓库根，不是"任何含 langs/ 的目录"：测试输出目录里也有一份 langs/ 拷贝，
        // 所以还得同时有 src/Andzip.App 才算。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "langs"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Andzip.App")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static double Luminance(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    // 取样与比较都在 Pixels 里（UiHarness.cs），这里不再重复一份读帧缓冲的代码。
    private static Color Sample(Window window, Point at) => Pixels.At(window, at);

    private static bool Same(Color a, Color b) => Pixels.Same(a, b);

    private static string Hex(Color c) => Pixels.Name(c);
}
