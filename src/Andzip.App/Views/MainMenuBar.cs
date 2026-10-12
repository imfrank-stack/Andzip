using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Avalonia.Controls;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>主菜单栏：由 <see cref="MainMenuModel"/>（需求文档附录 A 结构）渲染。</summary>
internal static class MainMenuBar
{
    public static Menu Create(LangCatalog lang, IReadOnlyDictionary<string, Action> commands)
    {
        var menu = new Menu
        {
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.Parse("#1F1F1F")),
            FontSize = 14,
            Padding = new Avalonia.Thickness(6, 3, 6, 3),
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
                };

                if (item.CommandId is not null && commands.TryGetValue(item.CommandId, out var action))
                {
                    menuItem.Click += (_, _) => action();
                }

                topItem.Items.Add(menuItem);
            }

            menu.Items.Add(topItem);
        }

        return menu;
    }
}