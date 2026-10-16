using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
/// <para><strong>「添加」「删除」「扫描」4.2.0 起是真的了</strong>：它们与「编辑 → 添加文件」
/// 「编辑 → 删除文件」「文件 → 扫描压缩包中的恶意软件」是<strong>同一条命令</strong>（同一个命令 id），
/// 所以灰与亮由窗口那一趟按命令的置灰统一决定 —— 两处各亮各灰，比两处都灰更糟。
/// 「查看」仍然没有命令：那是参考软件的预览面板，功能本身还没做。</para>
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
        // 九颗各自占一格、九格等宽铺满整张卡片（3.5.0，用户：「将首页的这9个按钮平均布局在整个软件宽度上」）。
        // 以前这里是一列 StackPanel：它只按每颗自己的内容宽度从左排，卡片明明拉满了整窗宽度，
        // 右边却空出一大截（真实窗口里量到的就是"九颗挤在左边、右侧一片白"）。
        // 换成一行九格的 UniformGrid 之后，可用宽度被切成九份、每格一样宽，图标与文字仍在格内居中，
        // 悬停那块方填充也就铺满整格。
        // 为什么不用九条 `*` 列的 Grid：实测那一种给的是**取整后不均匀**的宽度
        // （960 宽时量到 106 / … / 112），'九颗一样宽'这条 3.1.0 立的规矩当场就破了；
        // UniformGrid 每格同一尺寸（向上取整），代价是整排最多比卡片内沿多出不到九像素 ——
        // 那点落在卡片自己的内距里，看不见（见 ToolBarGlyphTests 那条铺满用例）。
        var items = new UniformGrid
        {
            Rows = 1,
            Columns = 9,
        };

        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_OPEN", "open-archive",
            MainMenuModel.CommandOpenArchive, actions.OpenArchive));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_EXTRACT", "extract",
            MainMenuModel.CommandExtractArchive, actions.ExtractArchive));

        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_NEW", "new-archive",
            MainMenuModel.CommandNewArchive, actions.NewArchive));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_ADD", "add-file",
            MainMenuModel.CommandAddEntries, actions.AddFiles));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_DELETE", "delete-file",
            MainMenuModel.CommandDeleteEntries, actions.DeleteFiles));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_TEST", "test-archive",
            MainMenuModel.CommandTestArchive, actions.TestArchive));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_SCAN", "scan-file",
            MainMenuModel.CommandScanArchive, actions.ScanArchive));
        // 「查看」= 参考软件的预览面板，功能本身还没做，所以这一格没有命令，也就没有 id：
        // 没有 id 就没人按命令去点亮它，它会一直灰着 —— 这正是想要的结果。
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_VIEW", "view", null, actions.ToggleViewStyle));

        // 语言切换本身在「设置 → 语言」里（换完要重启才全面生效），
        // 所以这一颗做的是"跳到那一页"。以前它写着当前语言、点不动，如今它有地方可去。
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_LANGUAGE", "language", null, actions.SwitchLanguage));

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

    /// <summary>
    /// 一格。<paramref name="commandId"/> 写进 <c>Tag</c>，好让窗口按命令把它点亮 / 置灰
    /// （与菜单项同一个记号、同一个口子）；<paramref name="run"/> 为 null 表示这条命令压根没接，
    /// 那一格建出来就是灰的，而且此后没人会去点亮它。
    /// </summary>
    private static Control Cell(LangCatalog lang, string textKey, string glyph, string? commandId, Action? run)
    {
        var icon = SvgGlyph.Load(glyph, Design.IconSize.ToolBar);
        var label = new TextBlock
        {
            Text = lang.Label(textKey),
            FontSize = Design.Type.Body,
            Foreground = Design.Ink.Primary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, Design.Space.X2 - 1, 0, 0),
            TextAlignment = TextAlignment.Center,
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { icon, label },
        };

        // 宽度交给外面那九等分的格子：这里不再写 MinWidth，写死一个下限只会在窄窗口上跟格子顶牛。
        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Design.Space.X3 + 2, Design.Space.X2, Design.Space.X3 + 2, Design.Space.X2),
            FontFamily = Design.Font,
            Tag = commandId,
            IsEnabled = run is not null,
        };

        if (run is { } action)
        {
            button.Click += (_, _) => action();
        }

        // 禁用态那副样子（图标降透明度、文字换禁用墨色、手型光标收掉）必须<strong>跟着
        // IsEnabled 现算</strong>，不能在建这一格时定一次：「添加」「删除」建的时候是亮着的
        // （命令接上了），回到首页要被按命令置灰 —— 定一次的结果是"文字灰了、图标还亮着"，
        // 而那条规矩写在本类开头：整格一起暗才读得出"这一格不通"。
        button.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsEnabledProperty)
            {
                PaintFace(button, icon, label);
            }
        };
        PaintFace(button, icon, label);

        // 悬停是一块平的淡色圆角方（3.3.0 参考图）：卡片自己已经浮起来了，格子不再各自带影。
        // 禁用时 Chrome 的事件根本不会触发（收不到指针事件），
        // 所以静止态保持透明，不会在卡片上留一块"亮着的死按钮"。
        return button.Tile();
    }

    private static void PaintFace(Button button, Control icon, TextBlock label)
    {
        icon.Opacity = button.IsEnabled ? 1 : DisabledOpacity;
        label.Foreground = button.IsEnabled ? Design.Ink.Primary : Design.Ink.Disabled;
        button.Cursor = button.IsEnabled
            ? new Cursor(StandardCursorType.Hand)
            : new Cursor(StandardCursorType.Arrow);
    }
}

/// <summary>
/// 工具栏各按钮的命令入口。<strong>null 表示这条命令压根没接</strong>（不是"这会儿不能点"——
/// 那归窗口按命令 id 现算，见 <c>MainWindow.CommandCarriers</c>），那一格建出来就是灰的。
/// </summary>
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

