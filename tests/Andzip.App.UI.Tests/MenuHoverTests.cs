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

    // 这里原本有三条用例钉着"指针离开菜单区域就把下拉收掉"（3.2.0 加的那个功能）。
    // 3.5.0 把那个功能撤了，用例跟着撤 —— 留着它们就等于要求一个我们不要再有的行为。
    // 为什么撤：真机上量到的是**指针挪进下拉里那一格，框也照样收**（见开发记录第七节第 76 条），
    // 而 Windows 自己的菜单从不因为指针离开而关。悬停置灰与弹窗圆角那几条用例仍然在上面。

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
