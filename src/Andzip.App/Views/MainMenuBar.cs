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

/// <summary>主菜单栏：由 <see cref="MainMenuModel"/>（需求文档附录 A 结构）渲染。</summary>
internal static class MainMenuBar
{
    public static Menu Create(LangCatalog lang, IReadOnlyDictionary<string, Action> commands, RecentFilesMenu? recent = null)
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
            var topItem = new MenuItem { Header = lang.Label(top.TextKey) };

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
                };

                if (item.CommandId is not null && commands.TryGetValue(item.CommandId, out var action))
                {
                    menuItem.Click += (_, _) => action();
                }

                if (item.CommandId == MainMenuModel.CommandRecentFiles && recent is not null)
                {
                    AttachRecentFiles(menuItem, lang, recent);
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
                var entry = new MenuItem { Header = path };
                ToolTip.SetTip(entry, path);
                entry.Click += (_, _) => recent.Open(path);
                host.Items.Add(entry);
            }

            host.Items.Add(new Separator());
            var clear = new MenuItem { Header = lang.Label("MENUSTR_CLEAR_RECENT") };
            clear.Click += (_, _) =>
            {
                recent.Clear();
                Rebuild();
            };
            host.Items.Add(clear);
        }
    }
}
