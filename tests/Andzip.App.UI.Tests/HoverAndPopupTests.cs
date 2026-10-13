using System.Collections.Generic;
using System.Linq;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Andzip.App.UiTests;

/// <summary>
/// 1.8.0 那一轮"悬停要浮起来、弹出框要圆润"的把关。
/// <para>全部量像素。读属性只能证明代码写了什么，证明不了屏幕上有没有 ——
/// 这一轮的每一条都对应一个"看着不对"的具体反馈。</para>
/// </summary>
[Collection("界面状态")]
public class HoverAndPopupTests
{
    /// <summary>弹窗测试用的画布色：既不是卡片白，也不是任何令牌色，免得把"没画"读成"画对了"。</summary>
    private static readonly Color Page = Color.FromRgb(0xD8, 0xDE, 0xE5);

    [AvaloniaFact]
    public void HoveredToolBarButtonBecomesARoundedBoxWithAShadow()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var button = VisualQuery.Of<Button>(window).First(b =>
            b.Content is StackPanel panel && panel.Orientation == Avalonia.Layout.Orientation.Vertical);
        var corner = button.TranslatePoint(new Point(6, 6), window)!.Value;
        var above = new Point(corner.X, button.TranslatePoint(new Point(0, -3), window)!.Value.Y);

        var restFace = Pixels.At(window, corner);
        var restAbove = Pixels.At(window, above);

        window.MouseMove(corner);
        Dispatcher.UIThread.RunJobs();

        var hoverFace = Pixels.At(window, corner);
        var hoverAbove = Pixels.At(window, above);

        Assert.True(Pixels.Same(restFace, Color.FromRgb(0xFF, 0xFF, 0xFF)),
            $"静止时按钮上就是卡片白，测量前提不成立（实测 {Pixels.Name(restFace)}）");
        Assert.True(Pixels.Same(hoverFace, Pixels.Of(Design.Surface.Muted)),
            $"悬停那颗药丸的底色不是 Surface.Muted（{Pixels.Name(Design.Surface.Muted)}），实测 {Pixels.Name(hoverFace)}");

        // 阴影落在按钮外沿：那一片本来一定是卡片白，暗了才说明影子真的画出来了
        Assert.True(Pixels.Same(restAbove, Color.FromRgb(0xFF, 0xFF, 0xFF)),
            $"按钮上方 3px 不是卡片白（{Pixels.Name(restAbove)}），测不到阴影");
        Assert.True(Darker(hoverAbove, restAbove),
            $"悬停时按钮外沿没有变暗（{Pixels.Name(restAbove)} → {Pixels.Name(hoverAbove)}）："
            + "用户要的是「带阴影的圆角框」，只改底色不算浮起来");
    }

    /// <summary>
    /// 菜单栏（文件 / 编辑 …）悬停：只轻微置灰。
    /// <para>SimpleTheme 给菜单项的高亮是 20% 品牌蓝，压在白色卡片上读起来像"这一项被选中了"
    /// 而不是"指针在这里"。这里钉住两件事：变成灰，而且不是那块蓝。</para>
    /// </summary>
    [AvaloniaFact]
    public void MenuBarHoverIsGreyNotTheThemesBlue()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var file = VisualQuery.Of<MenuItem>(window).First();

        // 取项里<strong>没有文字</strong>的那一头：中间那点会落在字形笔画上，
        // 量到的是"字与底的混合色"，跟任何令牌都对不上。
        var at = file.TranslatePoint(new Point(file.Bounds.Width - 3, file.Bounds.Height / 2), window)!.Value;

        var rest = Pixels.At(window, at);
        window.MouseMove(at);
        Dispatcher.UIThread.RunJobs();
        var hovered = Pixels.At(window, at);

        var grey = Pixels.Of(Design.Surface.Muted);
        Assert.False(Pixels.Same(rest, hovered), "菜单栏悬停一个像素都没变");
        Assert.True(Pixels.Same(hovered, grey),
            $"菜单栏悬停应当是 Surface.Muted 那档灰（{Pixels.Name(grey)}），实测 {Pixels.Name(hovered)}");
        Assert.True(hovered.B < hovered.R + 12 && hovered.G > hovered.B - 6,
            $"实测 {Pixels.Name(hovered)} 带蓝味，那是 SimpleTheme 自己的高亮没被换掉");
    }

    /// <summary>
    /// 子菜单弹出来是一枚圆角阴影面板，不是直角方块。
    /// <para>这条同时钉住实现路径：SimpleTheme 的弹窗根是一枚 <c>Border</c>，
    /// 所以圆角与阴影改它就够，不需要替整个 ControlTheme 重写模板。</para>
    /// </summary>
    [AvaloniaFact]
    public void SubmenuOpensAsARoundedShadowedPanel()
    {
        var (window, root) = OpenSubmenu();

        Assert.True(root.CornerRadius.TopLeft >= 6,
            $"子菜单的圆角只有 {root.CornerRadius}，用户要的是「不要有直角」");
        Assert.NotEqual(default, root.BoxShadow);

        var at = root.TranslatePoint(new Point(0, 0), window)!.Value;
        var corner = Pixels.At(window, new Point(at.X + 1, at.Y + 1));
        // 取列表左边缘那一竖条：再往里就是文字，量到的是墨色不是面板色
        var inside = Pixels.At(window, new Point(at.X + 3, at.Y + 14));

        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        Assert.True(Pixels.Same(inside, white),
            $"子菜单里面不是表面白（实测 {Pixels.Name(inside)}），测量不成立");
        // 圆角外的这一格不该是弹窗那张脸：要么透出画布，要么被自己的影子压暗，两者都比白暗
        Assert.True(Pixels.Luminance(corner) < Pixels.Luminance(white) - 0.08,
            $"子菜单左上角 1px 是 {Pixels.Name(corner)}，与面板白一样亮 —— 那还是个直角");

        var outside = Pixels.At(window, new Point(at.X + root.Bounds.Width + 4, at.Y + 40));
        Assert.True(Darker(outside, Page),
            $"子菜单外沿 4px 没有影子（{Pixels.Name(Page)} → {Pixels.Name(outside)}）");
    }

    /// <summary>下拉框的列表弹出来与子菜单同一套语言。</summary>
    [AvaloniaFact]
    public void DropDownListOpensAsARoundedShadowedPanel()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "甲", "乙", "丙" },
            SelectedIndex = 0,
            Width = 140,
            Margin = new Thickness(60, 70, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        }.Picker();
        var window = new Window
        {
            Content = new Panel { Children = { combo } },
            Width = 420,
            Height = 360,
            Background = new SolidColorBrush(Page),
        }.Shown();

        combo.IsDropDownOpen = true;
        UiWait.Pump(120);

        var root = OpenedPopupRoot(window);
        Assert.True(root.CornerRadius.TopLeft >= 6, $"下拉列表的圆角是 {root.CornerRadius}，还是直角");
        Assert.NotEqual(default, root.BoxShadow);

        var at = root.TranslatePoint(new Point(0, 0), window)!.Value;
        var corner = Pixels.At(window, new Point(at.X + 1, at.Y + 1));
        Assert.True(Pixels.Luminance(corner) < Pixels.Luminance(Pixels.Of(root.Background!)) - 0.08,
            $"下拉列表左上角 1px 是 {Pixels.Name(corner)}，与列表底色一样亮 —— 圆角没生效");
    }

    /// <summary>
    /// 提示气泡也换成圆角 + 阴影。
    /// <para>它和弹窗不是一条路：ToolTip 的模板里只有一枚 ContentPresenter，没有可改的 Border，
    /// 所以这里验的是"换模板这条路真的走通了"（<c>Chrome.InstallBubbles</c>）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TooltipBubbleIsRoundedAndCastAShadow()
    {
        var host = new Button { Content = "悬停我", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        ToolTip.SetTip(host, "这是一句提示");
        var window = new Window
        {
            Content = new Panel { Children = { host } },
            Width = 420,
            Height = 360,
            Background = new SolidColorBrush(Page),
        }.Shown();

        ToolTip.SetIsOpen(host, true);
        UiWait.Pump(120);

        var bubble = window.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault();
        Assert.NotNull(bubble);

        // 模板是"透明留白 + 里面那张脸"两层，要找的是后者
        var shell = bubble!.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.CornerRadius.TopLeft >= 4);
        Assert.True(shell is not null,
            "提示气泡的模板里没有圆角 Border —— Chrome.InstallBubbles 那层换模板没生效");

        var face = Pixels.At(window, shell.TranslatePoint(new Point(shell.Bounds.Width / 2, shell.Bounds.Height / 2), window)!.Value);
        var corner = Pixels.At(window, shell.TranslatePoint(new Point(1, 1), window)!.Value);
        Assert.True(Pixels.Same(face, Color.FromRgb(0xFF, 0xFF, 0xFF)),
            $"气泡里面不是表面白（实测 {Pixels.Name(face)}），测量不成立");
        // 圆角外的这一格不该还是那张脸。影子在这么小一颗气泡上读不出明显浓度
        // （实测外沿 4px 只差 1/255），所以这里钉的是"角是圆的"，不是"影子看得见"。
        Assert.False(Pixels.Same(corner, face),
            $"气泡左上角 1px 与面板同色（{Pixels.Name(corner)}）—— 那还是个直角");
    }

    /// <summary>
    /// 关闭按钮悬停：淡红底 + 红叉的一颗小药丸，不是整块实心红。
    /// <para>用户原话是"太突兀"。这块红是全屏面积最大的一块饱和色，而它表达的只是
    /// "点我会关掉窗口"。颜色换成 <c>DangerTint</c>，面积缩到 36×30 并带圆角。</para>
    /// </summary>
    [AvaloniaFact]
    public void CloseButtonHoverIsATintedChipNotASolidRedBlock()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var close = VisualQuery.Of<Button>(window).First(b =>
            Avalonia.Automation.AutomationProperties.GetName(b) == TestLang.Chinese().Label("TEXT_TITLEBAR_CLOSE"));

        // 药丸内缩 5px，所以取 (9,9) 那点：正中间正好是那个叉的交叉点，量到的是笔画色。
        var face = close.TranslatePoint(new Point(9, 9), window)!.Value;
        var edge = close.TranslatePoint(new Point(1, 1), window)!.Value;

        window.MouseMove(face);
        Dispatcher.UIThread.RunJobs();

        var tint = Pixels.Of(Design.Semantic.DangerTint);
        Assert.True(Pixels.Same(Pixels.At(window, face), tint),
            $"关闭按钮悬停的药丸底色不是 DangerTint（{Pixels.Name(tint)}），实测 {Pixels.Name(Pixels.At(window, face))}");
        Assert.True(Pixels.Same(Pixels.At(window, edge), Color.FromRgb(0xFF, 0xFF, 0xFF)),
            $"关闭按钮左上角 1px 也被刷上了（{Pixels.Name(Pixels.At(window, edge))}）—— 那还是整块方红");
    }

    /// <summary>
    /// 首页左栏那根线：换成最淡的一档，而且不顶天立地。
    /// <para>用户说"太粗了，不和谐"。量出来它只有 1px，所以问题不在粗细，
    /// 在于一根贯通上下的线把一整张表面读成了两个"框"。</para>
    /// </summary>
    [AvaloniaFact]
    public void TreeSplitterIsTheLightestHairlineAndDoesNotRunEdgeToEdge()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var line = window.GetVisualDescendants().OfType<Rectangle>()
            .First(r => r.Width == 1 && r.Fill is ISolidColorBrush);

        var painted = ((ISolidColorBrush)line.Fill!).Color;
        Assert.True(Pixels.Same(painted, Pixels.Of(Design.Line.Hair)),
            $"树栏分隔线不是最淡的 Line.Hair（{Pixels.Name(Design.Line.Hair)}），实测 {Pixels.Name(painted)}");
        Assert.True(line.Margin.Top >= 4 && line.Margin.Bottom >= 4,
            $"分隔线上下没有留白（Margin={line.Margin}），还是会顶天立地把面板切成两个框");

        var hair = Pixels.Luminance(Pixels.Of(Design.Line.Hair));
        var subtle = Pixels.Luminance(Pixels.Of(Design.Line.Subtle));
        Assert.True(hair > subtle, "Line.Hair 应当比 Line.Subtle 更淡，否则这一档没意义");
    }

    /// <summary>
    /// 弹出框的脸跟着调色板走 —— 深色下不能还是一块白板。
    /// <para>1.5.0 那份"深色下仍然偏浅"的清单里，菜单弹出层与下拉列表是两笔；
    /// 1.8.0 起那两块的底色归我们设了，所以这条断言现在要求它是<em>深</em>的。
    /// 菜单项自己的文字仍由 SimpleTheme 画（要连它一起换得重写 MenuItem 的 ControlTheme）。</para>
    /// </summary>
    [AvaloniaFact]
    public void SubmenuFaceFollowsThePaletteIntoDark()
    {
        Design.Use(dark: false);
        var (lightWindow, lightRoot) = OpenSubmenu();
        var light = Pixels.At(lightWindow, lightRoot, 5, 14);

        try
        {
            Design.Use(dark: true);
            var (darkWindow, darkRoot) = OpenSubmenu();
            var dark = Pixels.At(darkWindow, darkRoot, 5, 14);

            Assert.True(Pixels.Same(light, Color.FromRgb(0xFF, 0xFF, 0xFF)),
                $"浅色下弹窗的脸不是表面白（实测 {Pixels.Name(light)}），测量前提不成立");
            Assert.True(Pixels.Luminance(dark) < 0.2,
                $"深色下弹窗的脸还是 {Pixels.Name(dark)} —— 它没跟着调色板走，规范 §10.4 那条'弹出层偏浅'没被清掉");
        }
        finally
        {
            Design.Use(dark: false);
        }
    }

    private static (Window Window, Border Root) OpenSubmenu()
    {
        var menu = MainMenuBar.Create(TestLang.Chinese(), new Dictionary<string, System.Action>());

        // 菜单栏必须只占顶部那一行：Menu 自己会拉满整个 Panel，那样子菜单弹出来
        // 底下垫的还是菜单条的白，"圆角外应当透出背景"这一条就量不到了。
        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        var window = new Window
        {
            Content = dock,
            Width = 420,
            Height = 360,
            Background = new SolidColorBrush(Page),
        }.Shown();

        ((MenuItem)menu.Items[0]!).IsSubMenuOpen = true;
        UiWait.Pump(120);
        return (window, OpenedPopupRoot(window));
    }

    private static Border OpenedPopupRoot(Window window)
    {
        var popup = window.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.IsOpen);
        Assert.NotNull(popup);
        var root = popup!.Child as Border;
        Assert.True(root is not null, "弹窗的根不是 Border —— RoundPopups 那套实现前提变了，得重新量");
        return root!;
    }

    private static bool Darker(Color a, Color b) => Pixels.Luminance(a) < Pixels.Luminance(b) - 0.01;
}
