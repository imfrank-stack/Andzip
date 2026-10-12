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
/// <para>「打开」「解压」右侧原来各挂着一个下拉箭头（主按钮 + 小箭头两个按钮拼一组，
/// 没用 <c>SplitButton</c> 是因为 SimpleTheme 的模板自带底色，在蓝色带上会露出一块），
/// 0.9.12 删掉了：箭头里「最近的文件」根本没有这个功能，永远灰着，而「解压」里那一条
/// 与主按钮是同一条命令 —— 两个箭头点开都看不出差别，用户的评价是"似乎没有作用"。</para>
/// <para>尚未接入命令的按钮（添加 / 删除 / 扫描）保持禁用态，不假装可用。</para>
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

        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_OPEN"), ToolBarIcon.Open, actions.OpenArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_EXTRACT"), ToolBarIcon.Extract, actions.ExtractArchive));

        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_NEW"), ToolBarIcon.New, actions.NewArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_ADD"), ToolBarIcon.Add, actions.AddFiles));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_DELETE"), ToolBarIcon.Delete, actions.DeleteFiles));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_TEST"), ToolBarIcon.Test, actions.TestArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_SCAN"), ToolBarIcon.Scan, actions.ScanArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_VIEW"), ToolBarIcon.View, actions.ToggleViewStyle));

        // 当前语言：切换语言尚未实现，所以这里只是一个展示用的禁用按钮。
        items.Children.Add(CreateButton(
            "[" + lang.GetLanguageDisplayName(lang.CurrentLanguage) + "]",
            ToolBarIcon.None,
            null));

        return new Border { MinHeight = 96, Child = items };
    }

    private static Button CreateButton(string text, string icon, Action? run)
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
            Padding = new Thickness(13, 5, 13, 5),
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
