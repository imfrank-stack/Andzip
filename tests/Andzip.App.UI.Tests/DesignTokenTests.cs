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

        var ok = VisualQuery.Of<Button>(dialog).Single(b => Equals(b.Content?.ToString(), lang.Label("TEXT_BTN_OK")));
        var at = (ok.TranslatePoint(new Point(4, 4), dialog) ?? default);

        Assert.True(Same(Sample(dialog, at), BrandColor),
            $"主按钮底色不是品牌蓝（实测 {Hex(Sample(dialog, at))}）。");
    }

    private static readonly Color BrandColor = Color.FromRgb(0x0F, 0x6C, 0xBD);

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
