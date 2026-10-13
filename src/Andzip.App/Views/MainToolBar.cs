using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Andzip.App.Views;

/// <summary>
/// 主工具栏（FR-1 首页顶部那排大按钮）：图标 + 文字，与参考软件同构。
/// <para><strong>1.7.0 起它不再是那条蓝色带</strong>：整条带子改成坐在灰色画布上的白色圆角卡片，
/// 图标用品牌蓝、文字用正文墨色，悬停是一颗更深的圆角药丸。理由是层级表达方式变了 ——
/// 以前只有"底色差"可用（SimpleTheme 的模板控件不吃阴影），要区分工具栏与内容区只能靠一条
/// 实心蓝；现在卡片有真阴影（<c>ShadowCapabilityTests</c> 量的就是这件事），不需要用大面积高饱和
/// 色来喊"我在这里"。品牌蓝退到图标上，反而比糊一条带子更聚焦。</para>
/// <para>这里只交回按钮排本身，卡片那层外壳由 <c>MainWindow</c> 与菜单栏一起包 ——
/// 菜单和工具栏是同一块"外壳"，分成两张卡会把顶部切成三条横带。</para>
/// <para>「打开」「解压」右侧原来各挂着一个下拉箭头（主按钮 + 小箭头两个按钮拼一组），
/// 0.9.12 删掉了：箭头里「最近的文件」根本没有这个功能，永远灰着，而「解压」里那一条
/// 与主按钮是同一条命令 —— 两个箭头点开都看不出差别，用户的评价是"似乎没有作用"。</para>
/// <para>尚未接入命令的按钮（添加 / 删除 / 扫描）保持禁用态，不假装可用。</para>
/// </summary>
internal static class MainToolBar
{
    public static Control Create(LangCatalog lang, MainToolBarActions actions)
    {
        var items = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X1,
            Margin = new Thickness(Design.Space.X2, 0, Design.Space.X2, Design.Space.X2),
        };

        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_OPEN"), Icons.Open, actions.OpenArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_EXTRACT"), Icons.Extract, actions.ExtractArchive));

        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_NEW"), Icons.New, actions.NewArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_ADD"), Icons.Add, actions.AddFiles));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_DELETE"), Icons.Delete, actions.DeleteFiles));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_TEST"), Icons.Test, actions.TestArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_SCAN"), Icons.Scan, actions.ScanArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_VIEW"), Icons.View, actions.ToggleViewStyle));

        // 当前语言：切换语言尚未实现，所以这里只是一个展示用的禁用按钮。
        items.Children.Add(CreateButton(
            "[" + lang.GetLanguageDisplayName(lang.CurrentLanguage) + "]",
            string.Empty,
            null));

        return items;
    }

    private static Button CreateButton(string text, string icon, Action? run)
    {
        var enabled = run is not null;

        // 禁用态图标与文字同降 —— 只淡图标会读成"这个功能有，只是暂时不能点"，
        // 而事实是整条命令都不可用。
        var ink = enabled ? Design.Ink.Primary : Design.Ink.Disabled;

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children =
            {
                CreateIcon(icon, enabled ? Design.Brand.Base : Design.Ink.Disabled),
                new TextBlock
                {
                    Text = text,
                    FontSize = Design.Type.Body,
                    Foreground = ink,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, Design.Space.X2 - 1, 0, 0),
                    TextAlignment = TextAlignment.Center,
                },
            },
        };

        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Design.Space.X3 + 2, Design.Space.X2, Design.Space.X3 + 2, Design.Space.X2),
            MinWidth = 68,
            FontFamily = Design.Font,
            IsEnabled = enabled,
        };

        if (run is { } action)
        {
            button.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            button.Click += (_, _) => action();
        }

        // 圆角 + 悬停/按下。禁用时 Chrome 的事件根本不会触发（收不到指针事件），
        // 所以静止态保持透明，不会在卡片上留一块"亮着的死按钮"。
        return button.OnCard();
    }

    private static Control CreateIcon(string geometry, IBrush stroke)
    {
        if (string.IsNullOrEmpty(geometry))
        {
            // 语言按钮没有图标，占一个等高位保持基线一致。
            return new Border { Width = Design.IconSize.ToolBar, Height = Design.IconSize.ToolBar };
        }

        return Icons.Line(geometry, Design.IconSize.ToolBar, stroke);
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

