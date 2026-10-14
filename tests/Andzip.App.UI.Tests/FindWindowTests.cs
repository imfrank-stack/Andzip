using System;
using System.Linq;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Andzip.App.UiTests;

/// <summary>
/// 「查找 → 文件查找」那一扇窗口。
/// <para>要钉住的是三件在截图上看不出来的事：输入即过滤（不是点了按钮才搜）、
/// 结果与计数对得上、以及<strong>没有那两颗我们做不到的按钮</strong>
/// （参考软件那一排是 删除 / 解压 / 关闭；包内删成员引擎不支持，解压也不认"只解这几项"）。</para>
/// </summary>
[Collection("界面状态")]
public class FindWindowTests
{
    private static ArchiveEntryInfo File(string path, long size = 10)
        => new(path, NameOf(path), false, size, size, false, "Deflate", new DateTime(2026, 3, 4, 5, 6, 0));

    private static ArchiveEntryInfo Dir(string path)
        => new(path + "/", NameOf(path), true, 0, 0, false, null, null);

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static ArchiveListing Sample()
    {
        var entries = new[]
        {
            Dir("docs"),
            File("docs/readme.md"),
            File("docs/notes/todo.txt"),
            File("notes/README.txt"),
            File("readme.md"),
            File("assets/logo.png"),
        };

        return new ArchiveListing("sample.zip", entries, 4, 1, 40, 40, null);
    }

    /// <summary>
    /// 改过 <c>TextBox.Text</c> 之后要泵一次。
    /// <para>Avalonia 的 <c>TextChanged</c> 不是同步派发的 —— 实测赋完值那一瞬间计数还是 0，
    /// 跑完一轮 Dispatcher 才变 1。少了这一步，用例量到的是"输入之后还没生效的那一帧"，
    /// 报出来像"输入即过滤没做"，而做没做它自己并不知道。</para>
    /// </summary>
    private static void Type(Window window, string text)
    {
        VisualQuery.Of<TextBox>(window).Single().Text = text;
        UiWait.Pump(60);
    }

    [AvaloniaFact]
    public void TypingFiltersTheResultsWithoutAnyButton()
    {
        var lang = TestLang.Chinese();
        var window = new FindWindow(lang, Sample()).Shown();

        try
        {
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();

            Assert.Empty(list.Items?.OfType<object>().ToArray() ?? []);
            Assert.Contains(lang.Label("TEXT_FIND_EMPTY"), VisualQuery.VisibleTexts(window));

            Type(window, "readme");

            var paths = list.Items!.OfType<ArchiveEntryInfo>().Select(e => e.Path).ToArray();
            Assert.Equal(["docs/readme.md", "notes/README.txt", "readme.md"], paths);

            Assert.Contains("3", string.Join("|", VisualQuery.VisibleTexts(window)));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void NoMatchSaysSoInsteadOfShowingAnEmptyTable()
    {
        var lang = TestLang.Chinese();
        var window = new FindWindow(lang, Sample()).Shown();

        try
        {
            Type(window, "根本没有的东西");

            Assert.Contains(lang.Label("TEXT_FIND_NONE"), VisualQuery.VisibleTexts(window));
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 那一排只有「定位」与「关闭」，<strong>没有删除也没有解压</strong>。
    /// <para>参考软件上是 删除 / 解压 / 关闭。这两件我们做不到：往已有归档里删成员引擎不支持，
    /// 解压那条路只会整包解、不认选中的几项。摆上去就是两颗假按钮 ——
    /// 用户按下去要么什么都没发生，要么得到一份"只解了选中项"的错觉。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheDialogDoesNotOfferActionsWeCannotDo()
    {
        var lang = TestLang.Chinese();
        var window = new FindWindow(lang, Sample()).Shown();

        try
        {
            // 标题栏那三颗的内容是画出来的 Path/Border，读回 null —— 它们不是"动作按钮"，
            // 但也不能让一条 null 把整次比较炸掉（那会红得看不出为什么）。
            var labels = VisualQuery.Of<Button>(window)
                .Select(VisualQuery.TextOf)
                .Where(text => text is not null)
                .Select(text => text!)
                .ToArray();

            Assert.Contains(lang.Label("TEXT_BTN_REVEAL"), labels);
            Assert.Contains(lang.Label("TEXT_BTN_CLOSE"), labels);

            foreach (var forbidden in new[] { "MENUSTR_DELETE_FILE", "MENUSTR_EXTRACT_FILE", "TEXT_BTN_DELETE" })
            {
                Assert.DoesNotContain(lang.Label(forbidden), labels);
            }
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>「定位」要等有了选中才行；空选中的主按钮和一颗点不动的主按钮一样糟。</summary>
    [AvaloniaFact]
    public void RevealNeedsASelectionBeforeItCanBePressed()
    {
        var lang = TestLang.Chinese();
        var window = new FindWindow(lang, Sample()).Shown();

        try
        {
            var reveal = VisualQuery.Of<Button>(window)
                .Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_BTN_REVEAL")));
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();

            Assert.False(reveal.IsEnabled);

            Type(window, "logo");
            list.SelectedIndex = 0;
            UiWait.Pump(30);

            Assert.True(reveal.IsEnabled);
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 定位真的跳了层并选上那一行 —— 而且是<strong>深层</strong>的那一项：
    /// 详情列表只显示当前这一层，不先跳到 <c>docs/</c>，<c>docs/readme.md</c> 根本不在屏幕上。
    /// <para>这一条顺手挖出一个真 bug：<c>Navigate</c> 往树里写入选中状态时会同步回调
    /// <c>SelectionChanged</c>，那一刻 TreeView 报回来的"当前选中项"还是<strong>上一个</strong>节点，
    /// 于是那次回调把刚定下来的 <c>_current</c> 又改回去。用户点树看不出来（事件报的就是被点的那一项），
    /// 程序化跳层必然被弹回 —— 修法是 <c>_navigating</c> 重入守卫。</para>
    /// <para>断言问的是视图自己的状态（<c>CurrentFolderPath</c> / <c>SelectedRows</c>）而不是从窗口往下摸列表：
    /// 换层会把详情区整个重做一版，headless 里那张新列表要等一次真布局才挂进可视树。
    /// 屏幕上长什么样写在发布前的真机核对清单里。</para>
    /// </summary>
    [AvaloniaFact]
    public void RevealJumpsToTheFolderAndSelectsTheRow()
    {
        var lang = TestLang.Chinese();
        var listing = Sample();
        var view = new ArchiveView(lang, listing);
        var window = new Window { Content = view.Root, Width = 900, Height = 600 }.Shown();

        try
        {
            var target = listing.Entries.Single(e => e.Path == "docs/readme.md");

            Assert.Equal(string.Empty, view.CurrentFolderPath);
            Assert.True(view.RevealEntry(target), "没找到那一项所在的文件夹，定位没跳层");
            Assert.Equal("docs", view.CurrentFolderPath);
            Assert.Equal(["docs/readme.md"], view.SelectedRows.Select(e => e.Path).ToArray());
            Assert.True(ReferenceEquals(target, view.SelectedRows[0]), "选中的不是清单里那一个对象");

            // 这一项不在这份清单里时它得说实话，别返回 true 让调用方以为跳过去了。
            Assert.False(view.RevealEntry(File("nowhere/ghost.txt")));
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 五格各归各列，谁也不压谁 —— 表头那一行与每一条结果都要量。
    /// <para>这一条是真机核对抓出来的：<c>Cell(text, column, …)</c> 收下了 <c>column</c> 参数
    /// 却从没写进 <c>Grid.Column</c>，于是五格全落在第 0 列上。截图上表头照旧是"路径 压缩后大小 …"
    /// 一排字，行里却是五行文字叠成一坨 —— 而 headless 只读"这一格写着什么"，读不出"它在哪一列"，
    /// 所以前面那几条用例全绿。唯一能钉住它的办法是量坐标。</para>
    /// </summary>
    [AvaloniaFact]
    public void EveryCellLandsInItsOwnColumn()
    {
        var lang = TestLang.Chinese();
        var window = new FindWindow(lang, Sample()).Shown();

        try
        {
            Type(window, "readme");

            var grids = window.GetVisualDescendants().OfType<Grid>()
                .Where(grid => grid.Children.Count == 5 && grid.Children.All(child => child is TextBlock))
                .ToArray();

            Assert.True(grids.Length >= 2, $"只量到 {grids.Length} 张五列表格，表头与结果行至少该各一张");

            foreach (var grid in grids)
            {
                for (var i = 1; i < grid.Children.Count; i++)
                {
                    var previousRight = grid.Children[i - 1].Bounds.X + grid.Children[i - 1].Bounds.Width;
                    Assert.True(grid.Children[i].Bounds.X >= previousRight - 0.5,
                        $"第 {i} 格压在上一格上面（它的 x={grid.Children[i].Bounds.X}，上一格的右边界={previousRight}）");
                }
            }
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 回车就是「定位」，而且接在<strong>输入框</strong>上：单行 TextBox 把 Enter 吃掉了，
    /// 挂在窗口那一层的处理器根本收不到 —— 挂在窗口上等于没挂。
    /// </summary>
    [AvaloniaFact]
    public void EnterInTheQueryBoxRevealsTheFirstHit()
    {
        var lang = TestLang.Chinese();
        var window = new FindWindow(lang, Sample()).Shown();

        try
        {
            Type(window, "readme");

            var revealed = new List<ArchiveEntryInfo>();
            window.RevealRequested += entry => revealed.Add(entry);

            var box = VisualQuery.Of<TextBox>(window).Single();
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            UiWait.Pump(30);

            // 一条都没选 —— 回车跳第一条命中，而不是什么都不做。
            Assert.Equal(["docs/readme.md"], revealed.Select(e => e.Path).ToArray());

            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            list.SelectedIndex = 2;
            revealed.Clear();
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            UiWait.Pump(30);

            Assert.Equal(["readme.md"], revealed.Select(e => e.Path).ToArray());
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 定位之后<strong>左边的树要跟着走到那一层</strong>，不能只有列表跳了。
    /// <para>真机核对抓出来的两件事，headless 之前一条都看不见：</para>
    /// <para>① 深层那一项（<c>docs/notes/todo.txt</c>）只把最后一级 <c>IsExpanded</c> 打开了 ——
    /// 父级 <c>docs</c> 还收着，于是那一级根本不在屏幕上，选中与滚动都落空。
    /// 现在从根一路摊下来。</para>
    /// <para>② 上一个节点只是"被别的节点挤掉"，并没有被显式取消选中。TreeView 的单选是在容器
    /// 重新实现时回放的：查找窗口一关、主窗口重新拿到焦点，根节点带着自己那份"我是选中的"回来，
    /// 于是列表在 docs/notes，树的高亮退回压缩包根。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheTreeFollowsTheRevealEvenTwoLevelsDeep()
    {
        var lang = TestLang.Chinese();
        var listing = Sample();
        var view = new ArchiveView(lang, listing);
        var window = new Window { Content = view.Root, Width = 900, Height = 600 }.Shown();

        try
        {
            var tree = VisualQuery.Of<TreeView>(window).Single();
            var target = listing.Entries.Single(e => e.Path == "docs/notes/todo.txt");

            Assert.True(view.RevealEntry(target));

            Assert.True(Node(tree, "docs").IsExpanded, "通往 docs/notes 的那一级没收在父级里摊开");
            Assert.True(Node(tree, "docs/notes").IsSelected, "树没跟着选到定位去的那一层");
            Assert.False(Node(tree, string.Empty).IsSelected, "根节点还留着选中 —— 它会在下一次布局里把高亮抢回去");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>按节点路径在树里找那一格；<see cref="ArchiveFolderNode.Path"/> 为空就是压缩包根。</summary>
    private static TreeViewItem Node(TreeView tree, string path)
    {
        TreeViewItem? Walk(IEnumerable<Control> items)
        {
            foreach (var child in items.OfType<TreeViewItem>())
            {
                if (child.Tag is ArchiveFolderNode node && string.Equals(node.Path, path, StringComparison.Ordinal))
                {
                    return child;
                }

                if (Walk(child.Items.OfType<Control>()) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return Walk(tree.Items.OfType<Control>())
            ?? throw new InvalidOperationException($"树里找不到路径为 '{path}' 的那一格");
    }

    /// <summary>菜单项与快捷键：只有开着包才能查，Ctrl+F 绑的就是那一条。</summary>
    [AvaloniaFact]
    public void FindIsArchiveOnlyAndBoundToCtrlF()
    {
        Assert.Contains(MainMenuModel.CommandFindFiles, MainMenuModel.ArchiveOnlyCommands);

        var find = MainMenuModel.Create().Single(m => m.Id == "find").Items.Single();
        Assert.True(find.Enabled, "「文件查找」在模型里还是占位的禁用态");
        Assert.Equal("Ctrl+F", find.Shortcut);
        Assert.Equal(MainMenuModel.CommandFindFiles, find.CommandId);

        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();
        try
        {
            Assert.Contains(window.KeyBindings,
                b => Equals(b.Gesture, new KeyGesture(Key.F, KeyModifiers.Control)));
        }
        finally
        {
            Close(window);
        }
    }

    private static void Close(Window window)
    {
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
