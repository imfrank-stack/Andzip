using System;
using System.Collections.Generic;
using System.Linq;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Andzip.App.UiTests;

/// <summary>
/// 菜单项被鼠标扫过时那一格长什么样。
/// <para>用户的截图量的：浅灰底 <c>#F6F8FA</c> 是对的，外面那一圈 <c>#41B1E1</c> 蓝描边不是 ——
/// 菜单栏、导航药丸、工具栏按钮说的是"只轻微置灰"，唯独菜单项带一圈蓝框，
/// 于是"扫过"这件事在同一个软件里有两种长相。</para>
/// <para>每一条都<strong>自己收尾</strong>（关掉弹窗与窗口）。这一组与
/// <c>HoverAndPopupTests</c> 在同一个集合里串行跑，留着开着的弹窗与窗口不关，
/// 下一批命中栈断言就会量到上一组的残局 —— 第一版就是这么把邻居用例跑成偶发红的。</para>
/// </summary>
[Collection("界面状态")]
public class MenuHoverTests
{
    /// <summary>SimpleTheme 给 MenuItem 高亮配的那圈蓝（用户截图上量到的读数）。</summary>
    private static bool IsThemeBlue(Color c) => c.B > 170 && c.B - c.R > 40;

    [AvaloniaFact]
    public void HoveredMenuItemIsAFlatGreyPillWithoutABlueOutline()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var leaf = LeafOf(window);
            Hover(window, leaf);
            var face = FaceOf(window, leaf);

            var blue = SampleBand(window, leaf).Count(IsThemeBlue);
            Assert.True(blue == 0,
                $"悬停的菜单项上扫到 {blue} 个蓝像素 —— 面皮 BorderThickness={face.BorderThickness} "
                + $"BorderBrush={face.BorderBrush?.GetType().Name}，而不是扁平的灰药丸");

            var fill = face.Background is ISolidColorBrush solid ? solid.Color : Colors.White;
            Assert.True(Pixels.Luminance(fill) < Pixels.Luminance(Colors.White) - 0.005,
                $"悬停没有把那一格置灰（面皮底色 {fill}）");
        }
        finally
        {
            Close(window, menu);
        }
    }

    [AvaloniaFact]
    public void TheMenuBarItemHoversTheSameWayAsItsOwnItems()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var top = (MenuItem)menu.Items[0]!;

            // 指针从子菜单收回菜单栏那一项上：两处必须是同一种高亮。
            Hover(window, top);

            var blue = SampleBand(window, top).Count(IsThemeBlue);
            Assert.True(blue == 0,
                $"菜单栏那一项（{top.Header}）的高亮带蓝框 {blue} 像素，与下拉里的不一致");
        }
        finally
        {
            Close(window, menu);
        }
    }

    /// <summary>
    /// 「新建压缩文件」里「添加」那颗按钮的下拉，与主菜单用的是同一套高亮。
    /// <para>这一处从来没接过包装（<c>MenuGrayHover</c> 只挂在主菜单那十几项上），
    /// 而 <c>Chrome.MenuGrayHover</c> 找面皮靠的是模板里那枚 <c>Border#root</c> ——
    /// <strong>万一 <c>MenuFlyout</c> 里的项不是同一个模板，接了也是空转</strong>，
    /// 所以这一条要真的开一次那个下拉来量，而不是相信"都是 MenuItem"。
    /// 去掉那层包装它报 126 个蓝像素（比主菜单那 14 个多得多，因为没人挡过）。</para>
    /// </summary>
    [AvaloniaFact]
    public void AButtonFlyoutHoversTheSameWayAsAMenuItem()
    {
        var item = new MenuItem { Header = "添加文件" }.MenuGrayHover();
        var flyout = new MenuFlyout();
        flyout.Items.Add(item);

        var button = new Button { Content = "添加" }.Ghost();
        button.Flyout = flyout;

        var window = new Window
        {
            Content = new StackPanel { Children = { button } },
            Width = 520,
            Height = 420,
            Background = new SolidColorBrush(Page),
        }.Shown();

        try
        {
            flyout.ShowAt(button);
            UiWait.Pump(120);

            var face = FaceOf(window, item);
            Hover(window, item);

            var blue = SampleBand(window, item).Count(IsThemeBlue);
            Assert.True(blue == 0,
                $"「添加」下拉里的项悬停仍带蓝框 {blue} 像素（面皮 {face.BorderThickness}）");
        }
        finally
        {
            flyout.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// 悬停不许挪动那一排字（3.1.0 用户报的「框体闪烁」）。
    /// <para>真机量的：指针扫到「文件」那一刻，它自己的字往左上挪了 2 物理像素，
    /// 右边那几项整体往左平移 4 像素。<strong>字没有变大</strong>（同一批墨点的宽高完全相同，
    /// 量法见开发记录第七节那条"先量再改"），动的是位置 —— 而一整排跟着跳一下，
    /// 读起来就是用户说的"闪烁"。</para>
    /// <para>根因在 <c>Chrome.MenuGrayHover</c>：2.5.1 为了挡模板那圈蓝描边，顺手把
    /// <c>BorderThickness</c> 归零，可模板两态的描边本来都是 1px —— 于是悬停换掉的是几何，不是颜色。
    /// 所以这里两件事都要量：机制（面皮粗细两态相同）与结果（邻居的 X 不许动）。</para>
    /// </summary>
    [AvaloniaFact]
    public void HoveringAMenuItemDoesNotMoveTheRow()
    {
        var menu = MainMenuBar.Create(TestLang.Chinese(), new Dictionary<string, System.Action>());
        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        var window = new Window
        {
            Content = dock,
            Width = 900,
            Height = 700,
            Background = new SolidColorBrush(Page),
        }.Shown();

        try
        {
            var first = (MenuItem)menu.Items[0]!;
            var second = (MenuItem)menu.Items[1]!;
            UiWait.Pump(60);

            var face = FaceOf(window, first);
            var idleThickness = face.BorderThickness;
            var secondWasAt = second.TranslatePoint(new Point(), window)!.Value;
            var firstWasAt = first.TranslatePoint(new Point(), window)!.Value;

            Hover(window, first);

            Assert.True(face.BorderThickness == idleThickness,
                $"悬停把面皮的描边从 {idleThickness} 换成 {face.BorderThickness} —— 换几何就是换位置，"
                + "整排菜单会跟着跳一下（用户读作闪烁）");

            var secondIsAt = second.TranslatePoint(new Point(), window)!.Value;
            Assert.True(Math.Abs(secondIsAt.X - secondWasAt.X) < 0.5,
                $"悬停第一项之后，第二项从 x={secondWasAt.X:0.##} 挪到了 x={secondIsAt.X:0.##} —— 整排在平移");

            var firstIsAt = first.TranslatePoint(new Point(), window)!.Value;
            Assert.True(Math.Abs(firstIsAt.Y - firstWasAt.Y) < 0.5,
                $"悬停把「{first.Header}」自己从 y={firstWasAt.Y:0.##} 挪到 y={firstIsAt.Y:0.##}");
        }
        finally
        {
            window.MouseMove(new Point(window.Bounds.Width - 4, window.Bounds.Height - 4));
            window.Close();
            Dispatcher.UIThread.RunJobs();
            UiWait.Pump(30);
        }
    }

    /// <summary>
    /// 指针离开「菜单栏那一项 + 它开着的下拉」之后，下拉自己合上（3.2.0，用户报的）。
    /// <para>用户 2026-10-15 的话：「当我的鼠标离开『帮助』那个区域的时候，自动关闭框体」。
    /// Avalonia 的默认行为是"点开就一直挂着"，直到点别处或 Esc —— 而这块下拉压在主界面上，
    /// 指针一移开它就变成一块没人管的白板。</para>
    /// <para>后半条量的是<strong>连菜单模式一起退掉</strong>：只把 <c>IsSubMenuOpen</c> 置假，
    /// 这一项仍留在 <c>Menu</c> 的菜单模式里、还是 <c>:selected</c>，主题那圈蓝就一直挂着。
    /// 真机上量的：从没打开过 = 白 <c>FFFFFF</c>，按 Esc 关 = 白，指针离开关 = <c>CFECF8</c> 底
    /// 配 <c>41B1E1</c> 边。Esc 走的是菜单自己那条收菜单的路，所以这里也走它。</para>
    /// </summary>
    [AvaloniaFact]
    public void LeavingTheMenuAreaClosesTheDropDown()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var top = (MenuItem)menu.Items[0]!;

            // 指针先真的落在那一项上（用户点开菜单就是这个形状），再移开
            Hover(window, top);
            Assert.True(top.IsSubMenuOpen, "前提：菜单已经打开了。");

            window.MouseMove(new Point(window.Bounds.Width - 30, window.Bounds.Height - 30));
            Dispatcher.UIThread.RunJobs();
            UiWait.Pump(60);

            Assert.False(top.IsSubMenuOpen, "指针已经离开菜单区域，下拉还开着。");

            var blue = SampleBand(window, top).Count(IsThemeBlue);
            Assert.True(blue == 0,
                $"指针离开之后「{top.Header}」上还留着 {blue} 个蓝像素 —— 下拉合上了，"
                + "但这一项没退出菜单模式，仍带着主题那圈 :selected 蓝");
        }
        finally
        {
            Close(window, menu);
        }
    }

    /// <summary>
    /// 从菜单栏那一项往下挪进下拉里，菜单<strong>不许</strong>被关掉。
    /// <para>这一条是上一条的反面，也是它真正的难点：下拉与菜单栏那一项分属两处
    /// （弹窗住在本窗口的 <c>OverlayLayer</c> 上），指针跨过去的那一刻那一项必然收到一次
    /// <c>PointerExited</c> —— 照着事件关，菜单点不动。所以要量的是<strong>位置</strong>：
    /// 指针这会儿是不是还在这一项与它的弹窗围成的区域里。</para>
    /// </summary>
    [AvaloniaFact]
    public void MovingFromTheHeaderIntoTheDropDownKeepsItOpen()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var top = (MenuItem)menu.Items[0]!;
            Hover(window, top);

            var leaf = LeafOf(window);
            Hover(window, leaf);

            Assert.True(top.IsSubMenuOpen, $"指针从「{top.Header}」挪进了下拉（{leaf.Header}），菜单却被关掉了。");
        }
        finally
        {
            Close(window, menu);
        }
    }

    /// <summary>
    /// 指针从来没进过菜单区域时，鼠标动一下不许把菜单收掉。
    /// <para>键盘（Alt）开出来的菜单就是这个形状：用户的手不在鼠标上。这一条挡的是
    /// "关闭"那一半被写成"只要指针不在区域里就关" —— 那样键盘用户一碰鼠标菜单就没了。</para>
    /// </summary>
    [AvaloniaFact]
    public void AMenuOpenedWithoutThePointerSurvivesAMouseMove()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var top = (MenuItem)menu.Items[0]!;
            Assert.True(top.IsSubMenuOpen, "前提：菜单已经打开了。");

            // 指针一直停在离菜单很远的地方，只在远处挪一下
            window.MouseMove(new Point(20, window.Bounds.Height - 30));
            Dispatcher.UIThread.RunJobs();
            UiWait.Pump(60);
            window.MouseMove(new Point(40, window.Bounds.Height - 30));
            Dispatcher.UIThread.RunJobs();
            UiWait.Pump(60);

            Assert.True(top.IsSubMenuOpen, "指针压根没进过菜单区域，菜单却被鼠标的移动关掉了。");
        }
        finally
        {
            Close(window, menu);
        }
    }

    /// <summary>
    /// 指针<strong>只挪开一点点</strong>（停在下拉右沿外 20 DIP）时，下拉不许合上
    /// （3.5.0，用户的话：「做了一个比较蠢的功能，就是鼠标离开，弹窗消失 …… 改为鼠标离开距离较远时弹窗消失」）。
    /// <para>3.2.0 那版判据是"指针出界一像素就合"（余量只有 6 DIP）：从下拉边上滑出去一点点、
    /// 或者手只是抖了一下，菜单当场就没了，用户正在读的那一屏被收走，只能重新点开。
    /// 这一条量的正是那个"一点点" —— 指针离下拉 20 DIP，菜单必须还开着。</para>
    /// <para>把判据改回"出界就合"（<c>LeaveDistance</c> 归零 / 改回 6 DIP），这一条立刻红，
    /// 而「移到窗口另一角要合」那条照样绿 —— 两条守的是不同的机制：这条守"多近算还在一起"，
    /// 那条守"多远才算走开"。</para>
    /// </summary>
    [AvaloniaFact]
    public void MovingJustOutsideTheDropDownKeepsItOpen()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var top = (MenuItem)menu.Items[0]!;

            // 指针先真的落在那一项上（用户点开菜单就是这个形状）—— 少了这一步，
            // 那条"指针从来没进来过就不管"的闸门会让整件事量不到（第一版就是这么假绿的）。
            Hover(window, top);
            Assert.True(top.IsSubMenuOpen, "前提：菜单已经打开了。");

            var popup = window.GetVisualDescendants()
                .OfType<Popup>()
                .First(p => p is { IsOpen: true, Child: Visual });
            var face = (Visual)popup.Child!;
            var at = face.TranslatePoint(new Point(), window)!.Value;
            var probe = new Point(at.X + face.Bounds.Width + 20, at.Y + face.Bounds.Height / 2);

            Assert.True(probe.X < window.Bounds.Width && probe.X > 0,
                $"前提：取样点 {probe} 得落在窗口（{window.Bounds.Width:0.##} 宽）里，"
                + "否则测的是别的什么地方");

            window.MouseMove(probe);
            Dispatcher.UIThread.RunJobs();
            UiWait.Pump(60);

            Assert.True(top.IsSubMenuOpen,
                $"指针只挪到下拉右沿外 20 DIP（{probe}，下拉右沿 {at.X + face.Bounds.Width:0.##}），"
                + "下拉就合上了 —— 用户正读着的那一屏被收走");
        }
        finally
        {
            Close(window, menu);
        }
    }

    /// <summary>
    /// 指针挪到<strong>菜单栏这一行本身</strong>（右端那段空白，离下拉大半个窗口）时，菜单不许被收掉
    /// （3.5.0）—— 这一条守的是"整条菜单栏也算在里面"。
    /// <para>用户说的「鼠标离开，弹窗消失」最刺眼的一种就是它：指针只是沿着「文件 / 编辑 / 查看 …」
    /// 横向挪一下，整块菜单就没了。指针在这条行上时还没离开菜单这一带，判据把这一项所在的
    /// 整条 <see cref="MenuBase"/> 也算成"里面"，于是滑到哪一格都还在范围里。</para>
    /// <para>取样点特意选在<strong>离下拉足够远</strong>的那一头（先用前提断言把这件事钉住），
    /// 否则测到的会是"弹窗自己的近旁"，把菜单栏那一块的判据整个删掉它照样绿 ——
    /// 第一版就是这么白的（指针停在下拉右沿外 20 DIP，两条判据都罩得住那一点）。</para>
    /// </summary>
    [AvaloniaFact]
    public void MovingOntoTheMenuBarItselfKeepsTheMenuOpen()
    {
        var (window, menu) = OpenFileMenu();
        try
        {
            var first = (MenuItem)menu.Items[0]!;
            Hover(window, first);
            Assert.True(first.IsSubMenuOpen, "前提：菜单已经打开了。");

            var popup = window.GetVisualDescendants()
                .OfType<Popup>()
                .First(p => p is { IsOpen: true, Child: Visual });
            var face = (Visual)popup.Child!;
            var faceAt = face.TranslatePoint(new Point(), window)!.Value;
            var faceRight = faceAt.X + face.Bounds.Width;

            var probe = new Point(menu.Bounds.Width - 8, menu.Bounds.Height / 2);
            Assert.True(probe.X - faceRight > 100,
                $"前提：取样点 {probe} 离下拉右沿只有 {probe.X - faceRight:0.##} DIP —— "
                + "这么近的话，两条判据都罩得住它，这条用例就测不到'菜单栏整条算在里面'那件事"
                + $"（菜单栏量到的宽度是 {menu.Bounds.Width:0.##}）");

            window.MouseMove(probe);
            Dispatcher.UIThread.RunJobs();
            UiWait.Pump(60);

            Assert.True(first.IsSubMenuOpen,
                $"指针只是挪到菜单栏这一行上（{probe}），离下拉右沿还有 {probe.X - faceRight:0.##} DIP，"
                + "下拉就被收走了 —— 沿菜单栏横着挪一下菜单就没了");
        }
        finally
        {
            Close(window, menu);
        }
    }

    /// <summary>把「文件」菜单打开。返回窗口与那一条菜单栏，调用方负责 <see cref="Close"/>。</summary>
    private static (Window, Menu) OpenFileMenu()    {
        var menu = MainMenuBar.Create(TestLang.Chinese(), new Dictionary<string, System.Action>());

        // 菜单栏只占顶部那一行：Menu 自己会拉满整个 Panel，那样弹窗底下垫的还是菜单条的白。
        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        // 窗口要装得下整份「文件」菜单，否则弹窗被翻边，取样点落在别处。
        var window = new Window
        {
            Content = dock,
            Width = 900,
            Height = 700,
            Background = new SolidColorBrush(Page),
        }.Shown();

        ((MenuItem)menu.Items[0]!).IsSubMenuOpen = true;
        UiWait.Pump(120);
        return (window, menu);
    }

    private static void Close(Window window, Menu menu)
    {
        // 关掉自己开的东西：这一组与邻居共用同一个进程与同一个鼠标设备，
        // 留着展开的弹窗与停在菜单项上的指针，下一批命中测试量到的就是上一组的残局。
        ((MenuItem)menu.Items[0]!).IsSubMenuOpen = false;
        window.MouseMove(new Point(window.Bounds.Width - 4, window.Bounds.Height - 4));
        window.Close();
        Dispatcher.UIThread.RunJobs();
        UiWait.Pump(30);
    }

    /// <summary>
    /// 第一个可用的叶子项（「文件」里那条「打开压缩文件」）。
    /// <para>要从<strong>窗口</strong>往下找：弹窗住在本窗口的 OverlayLayer 上，
    /// 不是那一条 <c>Menu</c> 的后代，从菜单栏往下走一个都找不到。</para>
    /// </summary>
    private static MenuItem LeafOf(Visual scope)
        => scope.GetVisualDescendants()
            .OfType<MenuItem>()
            .First(m => m.Parent is not Menu && m.IsEnabled && m.Items.Count == 0);

    private static void Hover(Window window, MenuItem item)
    {
        var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        Dispatcher.UIThread.RunJobs();
        UiWait.Pump(60);

        Assert.True(item.IsPointerOver, $"指针没落到「{item.Header}」上，测的是别的东西");
    }

    /// <summary>模板里那枚负责高亮的 Border。找不到就是"接了包装也在空转"。</summary>
    private static Border FaceOf(Visual scope, MenuItem item)
        => scope.GetVisualDescendants().OfType<Border>()
            .First(b => b.Name == "root" && b.Parent == item);

    /// <summary>沿那一格的上下两条边与左右两条边各走一遍（蓝框就画在这四边上）。</summary>
    private static List<Color> SampleBand(Window window, MenuItem item)
    {
        var at = item.TranslatePoint(new Point(), window)!.Value;
        var colors = new List<Color>();

        for (var x = 2; x < item.Bounds.Width - 2; x += 2)
        {
            colors.Add(Pixels.At(window, new Point(at.X + x, at.Y + 1)));
            colors.Add(Pixels.At(window, new Point(at.X + x, at.Y + item.Bounds.Height - 2)));
        }

        for (var y = 2; y < item.Bounds.Height - 2; y += 2)
        {
            colors.Add(Pixels.At(window, new Point(at.X + 1, at.Y + y)));
            colors.Add(Pixels.At(window, new Point(at.X + item.Bounds.Width - 2, at.Y + y)));
        }

        return colors;
    }

    /// <summary>画布色：既不是卡片白，也不是任何令牌色，免得把"没画"读成"画对了"。</summary>
    private static readonly Color Page = Color.FromRgb(0xD8, 0xDE, 0xE5);
}
