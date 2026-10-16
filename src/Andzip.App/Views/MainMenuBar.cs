using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Avalonia.Controls;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「最近的文件」子菜单需要的三件事：读出现在的列表、打开其中一条、清空。
/// <para>用回调而不是把 <c>AppSettings</c> 与 <c>MainWindow</c> 传进菜单构造器：
/// 菜单栏不该知道设置存在哪、也不该知道打开一个包要走几步。</para>
/// </summary>
internal sealed record RecentFilesMenu(
    Func<IReadOnlyList<string>> Load,
    Action<string> Open,
    Action Clear);

/// <summary>
/// 「视图 → 列设置」那一排要的两件事：现在关着哪些列、把某一列打开或关掉。
/// <para>列的<strong>名单与标题不在这里</strong> —— 它们只有一个来源（<c>ArchiveView</c> 的列表定义），
/// 菜单照着那份枚举。两边各写一份的话，改日菜单上就会有一行"文件版本"而表头根本没有那一列。</para>
/// </summary>
internal sealed record ColumnMenu(
    Func<IReadOnlyList<string>> LoadHidden,
    Action<string, bool> SetVisible);

/// <summary>主菜单栏：由 <see cref="MainMenuModel"/>（需求文档附录 A 结构）渲染。</summary>
internal static class MainMenuBar
{
    public static Menu Create(LangCatalog lang, IReadOnlyDictionary<string, Action> commands,
        RecentFilesMenu? recent = null, ColumnMenu? columns = null)
    {
        var menu = new Menu
        {
            Background = Design.Surface.Base,
            Foreground = Design.Ink.Primary,
            FontFamily = Design.Font,
            FontSize = Design.Type.Body,
            Padding = new Avalonia.Thickness(Design.Space.X2, Design.Space.X1 + 1, Design.Space.X2, Design.Space.X1 + 1),
        };

        foreach (var top in MainMenuModel.Create())
        {
            var topItem = new MenuItem { Header = lang.Label(top.TextKey) }
                .MenuGrayHover().RoundOnOpen().CloseWhenPointerLeaves();

            foreach (var item in top.Items)
            {
                if (item.IsSeparator)
                {
                    topItem.Items.Add(new Separator());
                    continue;
                }

                var menuItem = new MenuItem
                {
                    Header = lang.Label(item.TextKey),
                    IsEnabled = item.Enabled,
                    InputGesture = GestureConverter.ToGesture(item.Gesture),
                    // Tag 存 CommandId，好让窗口在切换视图时按命令找到这一项置灰 / 点亮
                    // （「删除压缩文件」在首页上不该能点）。
                    Tag = item.CommandId,
                }.MenuGrayHover();

                if (item.CommandId is not null && commands.TryGetValue(item.CommandId, out var action))
                {
                    menuItem.Click += (_, _) => action();
                }

                if (item.CommandId == MainMenuModel.CommandRecentFiles && recent is not null)
                {
                    AttachRecentFiles(menuItem, lang, recent);
                }

                if (item.CommandId == MainMenuModel.CommandColumns && columns is not null)
                {
                    AttachColumns(menuItem, lang, columns);
                }

                topItem.Items.Add(menuItem);
            }

            menu.Items.Add(topItem);
        }

        return menu;
    }

    /// <summary>
    /// 把「最近的文件」变成一条真有内容的子菜单，并且在<strong>每次展开时重建</strong>。
    /// <para>一次性填充的话，用户打开第一个包之后菜单还是空的 —— 这份列表本来就是会变的。</para>
    /// </summary>
    private static void AttachRecentFiles(MenuItem host, LangCatalog lang, RecentFilesMenu recent)
    {
        host.RoundOnOpen();
        Rebuild();
        host.SubmenuOpened += (_, _) => Rebuild();

        void Rebuild()
        {
            host.Items.Clear();

            var paths = recent.Load();

            if (paths.Count == 0)
            {
                host.Items.Add(new MenuItem { Header = lang.Label("MENUSTR_RECENT_EMPTY"), IsEnabled = false });
                return;
            }

            foreach (var path in paths)
            {
                // 标题就是路径本身，不过 lang.Label / MnemonicText.Strip：
                // 文件名里可以合法出现 &，剥一下就把用户的路径改错了。
                var entry = new MenuItem { Header = path }.MenuGrayHover();
                ToolTip.SetTip(entry, path);
                entry.Click += (_, _) => recent.Open(path);
                host.Items.Add(entry);
            }

            host.Items.Add(new Separator());
            var clear = new MenuItem { Header = lang.Label("MENUSTR_CLEAR_RECENT") }.MenuGrayHover();
            clear.Click += (_, _) =>
            {
                recent.Clear();
                Rebuild();
            };
            host.Items.Add(clear);
        }
    }

    /// <summary>
    /// 「视图 → 列设置」：每一列一行，勾着 = 显示。每次展开重建（与「最近的文件」同一个理由：
    /// 关掉一列之后再看这排，勾必须已经变了，而不是等重启）。
    /// </summary>
    private static void AttachColumns(MenuItem host, LangCatalog lang, ColumnMenu columns)
    {
        // 这一层自己也是个子菜单：弹窗的面色是我们第一次展开时写上去的局部值，
        // 顶层那一次 RoundPopups 管不到它（它那时还没打开），所以这一项自己要挂一次。
        host.RoundOnOpen();
        Rebuild();
        host.SubmenuOpened += (_, _) => Rebuild();

        void Rebuild()
        {
            host.Items.Clear();

            var hidden = new HashSet<string>(columns.LoadHidden(), StringComparer.Ordinal);

            foreach (var (key, title) in ArchiveView.ToggleableColumns(lang))
            {
                var visible = !hidden.Contains(key);
                var item = new MenuItem
                {
                    Header = title,
                    Icon = visible ? Check() : null,
                }.MenuGrayHover();

                item.Click += (_, _) => columns.SetVisible(key, !visible);
                host.Items.Add(item);
            }
        }

        static Control Check() => new TextBlock
        {
            Text = "✓",
            FontSize = Design.Type.Body,
            Foreground = Design.Ink.Primary,
        };
    }
}
