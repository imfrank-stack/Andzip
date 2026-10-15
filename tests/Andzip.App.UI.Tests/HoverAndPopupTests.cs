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
using Avalonia.Input;
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

    /// <summary>SimpleTheme 在 :pressed 上涂给面皮的那块灰。</summary>
    private static readonly Color ThemeGray = Color.FromRgb(0x68, 0x68, 0x68);

    /// <summary>SimpleTheme 给 ComboBox 自带的那圈描边（比规范里的 Border.Strong 重两级）。</summary>
    private static readonly Color ThemeBorder = Color.FromRgb(0x88, 0x88, 0x88);

    /// <summary>
    /// 工具栏按钮的悬停：<strong>一块平的淡色圆角方填充</strong>（3.3.0，用户给的参考图）。
    /// <para>1.8.0 那一版是"带影子的圆角药丸"，当时整条工具栏还贴在面板上，格子浮一下是它唯一
    /// 的层级表达；3.3.0 起整条已经是一张浮在面板上的卡片，里面的格子再各自浮一次就是两层"浮"。
    /// 所以这里钉四件事：填的是 <c>Surface.Muted</c> 那档灰；填到上沿与左沿（整格都换档，不是只描一圈）；
    /// 左上角 1px 仍是卡片白（角是圆的，不是方块）；按钮上方 3px 仍是卡片白（<strong>没有影子</strong>）。</para>
    /// </summary>
    [AvaloniaFact]
    public void HoveredToolBarButtonIsAFlatRoundedSquareWithoutAShadow()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        UiWait.Pump(50);

        var button = VisualQuery.Of<Button>(window).First(b =>
            b.Content is StackPanel panel && panel.Orientation == Avalonia.Layout.Orientation.Vertical);

        Avalonia.Point At(double x, double y) => button.TranslatePoint(new Point(x, y), window)!.Value;
        var inside = At(6, 6);
        var topEdge = At(button.Bounds.Width / 2, 1);
        var leftEdge = At(1, button.Bounds.Height / 2);
        var cornerOut = At(1, 1);
        var above = At(button.Bounds.Width / 2, -3);

        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        Assert.True(Pixels.Same(Pixels.At(window, inside), white),
            $"静止时按钮上就是卡片白，测量前提不成立（实测 {Pixels.Name(Pixels.At(window, inside))}）");

        window.MouseMove(inside);
        Dispatcher.UIThread.RunJobs();

        var muted = Pixels.Of(Design.Surface.Muted);
        Assert.True(Pixels.Same(Pixels.At(window, inside), muted),
            $"悬停那一格的底色不是 Surface.Muted（{Pixels.Name(muted)}），实测 {Pixels.Name(Pixels.At(window, inside))}");
        Assert.True(Pixels.Same(Pixels.At(window, topEdge), muted),
            $"悬停的填充没到上沿（{Pixels.Name(Pixels.At(window, topEdge))}）—— 那一格读起来是描边不是填充");
        Assert.True(Pixels.Same(Pixels.At(window, leftEdge), muted),
            $"悬停的填充没到左沿（{Pixels.Name(Pixels.At(window, leftEdge))}）—— 那一格读起来是描边不是填充");

        // 圆角：(1,1) 落在半径 8 的圆弧外，应当还是卡片白；是方块的话这一格会被刷灰。
        Assert.True(Pixels.Same(Pixels.At(window, cornerOut), white),
            $"悬停那一格的左上角 1px 也被刷上了（{Pixels.Name(Pixels.At(window, cornerOut))}）—— 那是方块不是圆角方");

        // 平的：按钮外沿不许有影子。卡片自己已经浮起来了，格子再浮一次就是两层"浮"。
        Assert.True(Pixels.Same(Pixels.At(window, above), white),
            $"悬停时按钮上方 3px 变暗了（{Pixels.Name(Pixels.At(window, above))}）—— "
            + "用户要的是「淡色方形圆角淡色填充」，带影子的那一版是 1.8.0 的旧语言");
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

        var corner = Pixels.At(window, RoundedCornerOf(window, root));
        var at = root.TranslatePoint(new Point(0, 0), window)!.Value;
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

        var corner = Pixels.At(window, RoundedCornerOf(window, root));
        var at = root.TranslatePoint(new Point(0, 0), window)!.Value;
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

    /// <summary>
    /// 弹窗那圈留给阴影的透明留白<strong>不许盖住锚点</strong>。
    /// <para>1.8.0 给弹窗根加 <c>Margin = 12</c>（宿主按子元素尺寸裁剪，影子必须有地方画），
    /// 再用 <c>Popup.VerticalOffset -= 12</c> 把面板推回锚点 —— 结果弹窗矩形向上多出的那 12px
    /// 正好压在菜单栏那一排上。矩形是 light-dismiss 的判定范围：<strong>落在里面就不算"点到外面"，
    /// 于是这一格既关不掉菜单、也传不到菜单项</strong> —— 用户看到的"再点一次卡住"。</para>
    /// <para>这里不问"看起来对不对"，直接问命中测试：菜单展开时，鼠标停在锚点项的正中间，
    /// 命中的必须还是那个菜单项。</para>
    /// </summary>
    [AvaloniaFact]
    public void OpenSubmenuDoesNotStealClicksFromTheItemThatOpenedIt()
    {
        var menu = MainMenuBar.Create(TestLang.Chinese(), new Dictionary<string, System.Action>());
        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        // 窗口必须装得下整份「文件」菜单（约 343×348）：放不下时 Avalonia 会把弹窗
        // 往上挤/翻边，那时候住菜单项的是"窗口太小"这个测试环境，不是产品。
        var window = new Window
        {
            Content = dock,
            Width = 900,
            Height = 700,
            Background = new SolidColorBrush(Page),
        }.Shown();

        var item = (MenuItem)menu.Items[0]!;
        item.IsSubMenuOpen = true;
        UiWait.Pump(120);

        // headless 的命中测试只认**当前活动的那一扇窗口**：套件里别人开过、关过窗口之后，
        // 这一扇不再是活动窗口，`GetVisualsAt` 会返回空栈 —— 同一份几何，单独跑栈里有 6 层，
        // 全量跑到 171 条时读到 0 层。真机上没有这件事（用户点的就是前台那一扇），
        // 所以这里先把自己提活动，而不是把断言放宽。
        window.Activate();

        // 再等这一格真的"稳"下来：`RoundOnOpen` 把 `RoundPopups` 排到 Render 优先级（推迟一帧），
        // 没留白的那一版弹窗矩形正好压在锚点上，量到的是过渡帧而不是产品。
        // 判据用留白本身 —— `RoundPopups` 写过 Margin 就是处理完了（同一族写法见 RoundedCornerOf）。
        var settled = OpenedPopupRoot(window);
        var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        var deadline = Environment.TickCount64 + 2000;
        List<Visual> stack = [];
        var reachesTheItem = false;

        while (Environment.TickCount64 < deadline && !reachesTheItem)
        {
            UiWait.Pump(30);
            if (settled.Margin.Right == 0)
            {
                continue;
            }

            stack = window.GetVisualsAt(center).ToList();

            // GetVisualsAt 给的是"命中的可视元素"链，模板里的 AccessText / Border 在内而 MenuItem
            // 本身不在，所以判据是"从最上面那个往上走能不能走到这一项"，不是"栈里有没有 MenuItem 类型"。
            for (Visual? at = stack.FirstOrDefault(); at is not null; at = at.GetVisualParent())
            {
                if (ReferenceEquals(at, item))
                {
                    reachesTheItem = true;
                    break;
                }
            }
        }

        Assert.True(reachesTheItem,
            $"菜单展开时点在打开它的那一项正中间，命中的却不是这一项（从上到下："
            + $"{string.Join(" → ", stack.Select(v => $"{v.GetType().Name}{v.Bounds}").DefaultIfEmpty("空"))}）—— "
            + "弹窗把锚点盖住了，这一格点了什么也不会发生");
    }

    /// <summary>
    /// 点开菜单、鼠标滑进子菜单之后，那一项的高亮<strong>仍然是圆角的</strong>。
    /// <para>1.8.2 及之前：高亮只在指针进入时写，指针一滑进子菜单就 <c>ClearValue</c> 交还给
    /// SimpleTheme，模板立刻画回它自己那块<strong>直角</strong>的 20% 蓝 —— 用户看到的
    /// "扫过时圆角，点击变成直角"就是这一格之差。</para>
    /// </summary>
    [AvaloniaFact]
    public void OpenedMenuItemKeepsTheRoundedHighlightAfterThePointerLeaves()
    {
        var (window, _) = OpenSubmenu();
        var item = window.GetVisualDescendants().OfType<MenuItem>().First(m => m.IsSubMenuOpen);
        var face = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "root" && b.Parent == item);

        // 把指针挪到弹窗外面（既不在项上、也不在高亮上）
        window.MouseMove(new Point(window.Bounds.Width - 20, window.Bounds.Height - 20));
        Dispatcher.UIThread.RunJobs();

        Assert.False(item.IsPointerOver, "指针这时不该还在那一项上");
        Assert.True(item.IsSubMenuOpen, "指针移开把子菜单关掉了，测不到展开态");
        Assert.True(face.CornerRadius.TopLeft >= 4,
            $"展开中的菜单项退回成直角高亮（{face.CornerRadius}）—— 那就是用户说的\"点击变成直角\"");
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

    /// <summary>
    /// 弹窗左上角那一格，但<strong>等到圆角真的画上去</strong>再取样。
    /// <para><c>RoundOnOpen</c> 与 <c>DropDownOpened</c> 都是把 <c>RoundPopups</c> 排到 Render 优先级
    /// （推迟一帧，实现里写了为什么），所以固定泵 120ms 偶尔会抢在那一帧之前 —— 用例于是偶发红，
    /// 红的还是"角上仍是白的"这种假象（实测约四次一次）。等它变暗，最多两秒；
    /// 等不到就把原样交回断言，那时红的是产品，不是取样时机。</para>
    /// </summary>
    private static Point RoundedCornerOf(Window window, Border root)
    {
        var at = root.TranslatePoint(new Point(0, 0), window)!.Value;
        var probe = new Point(at.X + 1, at.Y + 1);
        var face = root.Background is { } brush ? Pixels.Luminance(Pixels.Of(brush)) : 1d;
        var deadline = Environment.TickCount64 + 2000;

        while (Environment.TickCount64 < deadline && Pixels.Luminance(Pixels.At(window, probe)) > face - 0.08)
        {
            UiWait.Pump(30);
        }

        return probe;
    }

    /// <summary>
    /// 下拉框右边那一格箭头：1.8.8 记下的一处"已知未修"，1.9.0 修掉了。
    /// <para>那块灰<strong>不在下拉框自己的面皮上</strong>，而在模板里内嵌的那枚
    /// <c>ToggleButton</c>（箭头）<strong>自己的</strong>面皮上 —— 只钉外层那枚够不着，
    /// 实测按住时那一格里有 445 个 <c>#686868</c>。用户报的"按下漏出黑色背景"是同一族，
    /// 所以这一处不能再留着。</para>
    /// <para>顺带量描边：主题给 ComboBox 自带一圈 <c>#888888</c>（1 DIP 实线），
    /// 比同一张表单里输入框那档 <c>Border.Strong</c> 重两级 —— 与 1.8.5 那圈树描边同一族。</para>
    /// <para>量的是<strong>整片</strong>而不是一粒像素：箭头那格只有 16 DIP 宽，
    /// 圆角与图标描边都会让单点取样碰巧躲过去。</para>
    /// </summary>
    [AvaloniaFact]
    public void PressingTheComboBoxArrowDoesNotShowTheThemeGray()
    {
        var window = new NewArchiveWindow(TestLang.Chinese()).Shown();
        var combo = VisualQuery.Of<ComboBox>(window).First();
        var at = combo.TranslatePoint(new Point(combo.Bounds.Width - 8, combo.Bounds.Height / 2), window)!.Value;
        Pixels.At(window, at);

        window.MouseMove(at);
        UiWait.Pump(60);
        window.MouseDown(at, MouseButton.Left);
        UiWait.Pump(120);

        var gray = 0;
        for (var dy = -14; dy <= 14; dy++)
        {
            for (var dx = -14; dx <= 14; dx++)
            {
                if (Pixels.Same(Pixels.At(window, at + new Point(dx, dy)), ThemeGray))
                {
                    gray++;
                }
            }
        }

        window.MouseUp(at, MouseButton.Left);
        UiWait.Pump(60);
        // 按下去已经把下拉列表弹出来了。**必须收回去再走**：1.8.6 那条"开着对话框的用例会把
        // 测试主机打崩"是同一件事，而这次的表现是下一个用例（子菜单圆角）偶发红 ——
        // 一个开着的弹窗留在没人关的窗口里，会污染后面按"当前有没有开着的弹窗"取样的用例。
        combo.IsDropDownOpen = false;
        UiWait.Pump(60);

        Assert.True(gray == 0,
            $"按住下拉框的箭头，那一格里有 {gray} 个 #686868 像素（主题那块灰） —— "
            + "灰是涂在内嵌那枚 ToggleButton 的面皮上的，Chrome.PinFace 得连着它一起钉");

        // 描边：沿着下边缘走一遍，主题那圈 #888888 一个像素都不该出现。
        var box = combo.TranslatePoint(new Point(0, combo.Bounds.Height - 1), window)!.Value;
        var heavy = 0;
        for (var x = 4; x < combo.Bounds.Width - 4; x++)
        {
            if (Pixels.Same(Pixels.At(window, new Point(box.X + x, box.Y)), ThemeBorder))
            {
                heavy++;
            }
        }

        Assert.True(heavy == 0,
            $"下拉框的下边缘有 {heavy} 个主题的 #888888 描边像素 —— "
            + "同一张表单里的输入框是 Border.Strong，两种描边读起来就是\"这个框被加重了\"");
    }

}
