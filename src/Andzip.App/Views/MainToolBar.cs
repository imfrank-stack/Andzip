using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Andzip.App.Views;

/// <summary>
/// 主工具栏（FR-1 首页顶部的蓝色带）：图标 + 文字的大按钮，与参考软件同构。
/// <para>
/// 「打开」「解压」带下拉箭头：主按钮执行默认动作，箭头弹出同组命令。用两个按钮拼成一组
/// 而不是 <c>SplitButton</c>，是因为 SimpleTheme 的 SplitButton 模板自带边框与内边距，
/// 放在蓝色带上会露出一块与背景不符的底色。
/// </para>
/// <para>尚未接入命令的按钮（添加 / 删除 / 测试 / 扫描 / 语言）保持禁用态，不假装可用。</para>
/// </summary>
internal static class MainToolBar
{
    private static readonly IBrush Normal = Brushes.White;
    private static readonly IBrush Dimmed = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));

    public static Control Create(LangCatalog lang, MainToolBarActions actions)
    {
        var items = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(10, 6, 10, 8),
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        items.Children.Add(CreateGroup(
            lang.Get("TEXT_TOOLBAR_OPEN"),
            ToolBarIcon.Open,
            actions.OpenArchive,
            () => Flyout(lang, (lang.Get("MENUSTR_OPEN"), actions.OpenArchive), (lang.Get("MENUSTR_RECENT"), null))));

        items.Children.Add(CreateGroup(
            lang.Get("TEXT_TOOLBAR_EXTRACT"),
            ToolBarIcon.Extract,
            actions.ExtractArchive,
            () => Flyout(lang, (lang.Get("MENUSTR_EXTRACT_FILE"), actions.ExtractArchive))));

        items.Children.Add(CreateButton(lang.Get("TEXT_TOOLBAR_NEW"), ToolBarIcon.New, actions.NewArchive));
        items.Children.Add(CreateButton(lang.Get("TEXT_TOOLBAR_ADD"), ToolBarIcon.Add, actions.AddFiles));
        items.Children.Add(CreateButton(lang.Get("TEXT_TOOLBAR_DELETE"), ToolBarIcon.Delete, actions.DeleteFiles));
        items.Children.Add(CreateButton(lang.Get("TEXT_TOOLBAR_TEST"), ToolBarIcon.Test, actions.TestArchive));
        items.Children.Add(CreateButton(lang.Get("TEXT_TOOLBAR_SCAN"), ToolBarIcon.Scan, actions.ScanArchive));
        items.Children.Add(CreateButton(lang.Get("TEXT_TOOLBAR_VIEW"), ToolBarIcon.View, actions.ToggleViewStyle));

        // 当前语言：切换语言尚未实现，所以这里只是一个展示用的禁用按钮。
        items.Children.Add(CreateButton(
            "[" + lang.GetLanguageDisplayName(lang.CurrentLanguage) + "]",
            ToolBarIcon.None,
            null));

        return new Border { MinHeight = 96, Child = items };
    }

    private static MenuFlyout Flyout(LangCatalog lang, params (string Text, Action? Run)[] items)
    {
        var flyout = new MenuFlyout();
        foreach (var (text, run) in items)
        {
            var item = new MenuItem { Header = MnemonicText.Strip(text), IsEnabled = run is not null };
            if (run is not null)
            {
                item.Click += (_, _) => run();
            }

            flyout.Items.Add(item);
        }

        return flyout;
    }

    /// <summary>主按钮 + 右侧小箭头的组合（参考软件的「打开 / 解压」样式）。</summary>
    private static Control CreateGroup(string text, string icon, Action? run, Func<MenuFlyout> flyout)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        panel.Children.Add(CreateButton(text, icon, run, narrow: true));

        var arrow = new Button
        {
            Content = new Path
            {
                Data = Geometry.Parse("M0,0 L5,5.5 L10,0"),
                Stroke = run is null ? Dimmed : Normal,
                StrokeThickness = 1.5,
                Width = 10,
                Height = 6,
                Stretch = Stretch.None,
            },
            Padding = new Thickness(3),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsEnabled = run is not null,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 14),
        };
        ToolTip.SetTip(arrow, text);

        if (run is not null)
        {
            arrow.Click += (_, _) => flyout().ShowAt(arrow);
        }

        panel.Children.Add(arrow);
        return panel;
    }

    private static Button CreateButton(string text, string icon, Action? run, bool narrow = false)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children =
            {
                CreateIcon(icon, run is null ? Dimmed : Normal),
                new TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    Foreground = run is null ? Dimmed : Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 0),
                },
            },
        };

        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(narrow ? 10 : 13, 5, narrow ? 8 : 13, 5),
            MinWidth = 66,
            IsEnabled = run is not null,
        };

        if (run is not null)
        {
            button.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            button.Click += (_, _) => run();
        }

        return button;
    }

    private static Control CreateIcon(string geometry, IBrush stroke)
    {
        if (string.IsNullOrEmpty(geometry))
        {
            // 语言按钮没有图标，占一个等高位保持基线一致。
            return new Border { Width = 40, Height = 40 };
        }

        return new Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = stroke,
            StrokeThickness = 1.5,
            Stretch = Stretch.Uniform,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}

/// <summary>工具栏各按钮的命令入口；null 表示该按钮当前不可用。</summary>
internal sealed record MainToolBarActions(
    Action? OpenArchive,
    Action? ExtractArchive,
    Action? NewArchive,
    Action? AddFiles = null,
    Action? DeleteFiles = null,
    Action? TestArchive = null,
    Action? ScanArchive = null,
    Action? ToggleViewStyle = null);

/// <summary>工具栏线稿图标（24×24 视框，白色描边）。</summary>
internal static class ToolBarIcon
{
    public const string None = "";

    /// <summary>打开：文件夹 + 向右箭头。</summary>
    public const string Open = "M2,5 h7 l2,2 h11 v13 h-20 z M9,14 h7 M13,11 l3.4,3 -3.4,3";

    /// <summary>解压：抽屉 + 向下箭头（把内容取出来）。</summary>
    public const string Extract = "M3,13 h18 v9 h-18 z M12,2 v9 M8,7.5 l4,4 4,-4 M7,16 h4 M14,16 h3";

    /// <summary>新建：带拉链的空白压缩包。</summary>
    public const string New = "M5,3 h14 v19 h-14 z M12,3 v19 M12,7 h2.6 M9.4,10 h2.6 M12,13 h2.6 M9.4,16 h2.6";

    /// <summary>添加：文件 + 加号。</summary>
    public const string Add = "M5,3 h10 v19 h-10 z M15,3 v5 h5 M17.5,12 v7 M14,15.5 h7";

    /// <summary>删除：文件 + 减号。</summary>
    public const string Delete = "M5,3 h10 v19 h-10 z M15,3 v5 h5 M14,16 h7";

    /// <summary>测试：闪电（快速校验）。</summary>
    public const string Test = "M13.5,2 L6,13 h5 l-1.5,9 L18,10 h-5 z";

    /// <summary>扫描：盾牌 + 对勾。</summary>
    public const string Scan = "M12,2 L4,5 v7 c0,5 3.4,8.4 8,10 c4.6,-1.6 8,-5 8,-10 v-7 z M8,12 l3,3 l5.5,-6";

    /// <summary>查看：列表线条。</summary>
    public const string View = "M3,5 h3 v3 h-3 z M9,6 h12 M3,11 h3 v3 h-3 z M9,12 h12 M3,17 h3 v3 h-3 z M9,18 h12";
}
