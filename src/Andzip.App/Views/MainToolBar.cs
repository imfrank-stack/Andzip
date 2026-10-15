using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 主工具栏（FR-1 首页顶部那排大按钮）：图标 + 文字，与参考软件同构。
/// <para><strong>1.7.0 起它不再是那条蓝色带</strong>：整条带子改成坐在灰色画布上的白色圆角卡片，
/// 图标用品牌蓝、文字用正文墨色，悬停是一颗更深的圆角药丸。理由是层级表达方式变了 ——
/// 以前只有"底色差"可用（SimpleTheme 的模板控件不吃阴影），要区分工具栏与内容区只能靠一条
/// 实心蓝；现在卡片有真阴影（<c>ShadowCapabilityTests</c> 量的就是这件事），不需要用大面积高饱和
/// 色来喊"我在这里"。品牌蓝退到图标上，反而比糊一条带子更聚焦。</para>
/// <para><strong>3.3.0 起这张卡片是工具栏自己的</strong>（用户给的参考图）：整条浮在面板上，
/// <strong>四周</strong>一圈淡影 + 圆角，与面板之间靠影子和留白分界，不再靠一根发丝线；
/// 悬停也从"带影子的圆角药丸"换成<strong>一块平的淡色圆角方填充</strong>（<see cref="Chrome.Tile"/>）——
/// 卡片自己已经浮起来了，里面的格子再各自浮一次就是两层"浮"，读起来是脏不是层次。</para>
/// <para>「打开」「解压」右侧原来各挂着一个下拉箭头（主按钮 + 小箭头两个按钮拼一组），
/// 0.9.12 删掉了：箭头里「最近的文件」根本没有这个功能，永远灰着，而「解压」里那一条
/// 与主按钮是同一条命令 —— 两个箭头点开都看不出差别，用户的评价是"似乎没有作用"。</para>
/// <para>尚未接入命令的按钮（添加 / 删除 / 扫描 / 查看）保持禁用态，不假装可用。</para>
/// <para><strong>2.6.0 起这九枚图标不再由代码画</strong>：几何来自 <c>assets/svg/*.svg</c>
/// （用户给的设计稿），由 <see cref="SvgGlyph"/> 在运行时读那份原文件画出来 ——
/// 抄进代码就等于造第二份真相。文字也跟着稿子改成长标签（「打开压缩包」而不是「打开」），
/// 因为这一排现在是"图标 + 一句话"，短到两个字的标签反而要人猜。</para>
/// </summary>
internal static class MainToolBar
{
    /// <summary>禁用态那枚双色图标整体降到的不透明度。文字走 <c>Ink.Disabled</c>，图标没有对应的"禁用第二档灰"，压透明度比换个颜色更贴近"这一整格都不通"。</summary>
    private const double DisabledOpacity = 0.4;

    public static Control Create(LangCatalog lang, MainToolBarActions actions)
    {
        var items = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X1,
        };

        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_OPEN"), "open-archive", actions.OpenArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_EXTRACT"), "extract", actions.ExtractArchive));

        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_NEW"), "new-archive", actions.NewArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_ADD"), "add-file", actions.AddFiles));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_DELETE"), "delete-file", actions.DeleteFiles));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_TEST"), "test-archive", actions.TestArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_SCAN"), "scan-file", actions.ScanArchive));
        items.Children.Add(CreateButton(lang.Label("TEXT_TOOLBAR_VIEW"), "view", actions.ToggleViewStyle));

        // 语言切换本身在「设置 → 语言」里（换完要重启才全面生效），
        // 所以这一颗做的是"跳到那一页"。以前它写着当前语言、点不动，如今它有地方可去。
        items.Children.Add(CreateButton(
            lang.Label("TEXT_TOOLBAR_LANGUAGE"), "language", actions.SwitchLanguage));

        // 整条是一张浮在面板上的卡片：底色与面板同白，分界全靠四周那一圈淡影与圆角。
        // 阴影用 Lift 那档（"浮在面板上的一块面"）而不是整窗那档 Floating ——
        // 这一条只有几十像素高，大弥散影会在旁边糊出一圈灰雾（Design.Shadow 那条注释量过）。
        return new Border
        {
            Background = Design.Surface.Base,
            CornerRadius = Design.Radius.Lg,
            BoxShadow = Design.Shadow.Lift,
            Padding = new Thickness(Design.Space.X2, Design.Space.X1, Design.Space.X2, Design.Space.X1),
            Child = items,
        };
    }

    private static Control CreateButton(string text, string glyph, Action? run)
    {
        var enabled = run is not null;

        // 禁用态图标与文字同降 —— 只淡图标会读成"这个功能有，只是暂时不能点"，
        // 而事实是整条命令都不可用。
        var ink = enabled ? Design.Ink.Primary : Design.Ink.Disabled;
        var icon = SvgGlyph.Load(glyph, Design.IconSize.ToolBar);

        if (!enabled)
        {
            icon.Opacity = DisabledOpacity;
        }

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children =
            {
                icon,
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

        // 悬停是一块平的淡色圆角方（3.3.0 参考图）：卡片自己已经浮起来了，格子不再各自带影。
        // 禁用时 Chrome 的事件根本不会触发（收不到指针事件），
        // 所以静止态保持透明，不会在卡片上留一块"亮着的死按钮"。
        return button.Tile();
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
    Action? ToggleViewStyle = null,
    Action? SwitchLanguage = null);

