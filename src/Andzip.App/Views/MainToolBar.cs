using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
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
/// 「查看」4.6.0 补上，是这一排最后一颗死占位。
/// <strong>4.7.0 那颗「扫描」换成了「CRC」</strong>（用户要求）：它点开的下拉与
/// 「文件 → CRC」是同一份名单（<see cref="CrcItems"/>），所以这一排里第一次有一颗
/// 带的不是动作而是一份选择 —— 它有命令 id、没有 run，亮与灰仍然走同一趟。</para>
/// <para><strong>2.6.0 起这九枚图标不再由代码画</strong>：几何来自 <c>assets/svg/*.svg</c>
/// （用户给的设计稿），由 <see cref="SvgGlyph"/> 在运行时读那份原文件画出来 ——
/// 抄进代码就等于造第二份真相。文字也跟着稿子改成长标签（「打开压缩包」而不是「打开」），
/// 因为这一排现在是"图标 + 一句话"，短到两个字的标签反而要人猜。</para>
/// </summary>
internal static class MainToolBar
{
    /// <summary>禁用态那枚双色图标整体降到的不透明度。文字走 <c>Ink.Disabled</c>，图标没有对应的"禁用第二档灰"，压透明度比换个颜色更贴近"这一整格都不通"。</summary>
    private const double DisabledOpacity = 0.4;

    /// <summary>
    /// 做一条工具栏。<paramref name="mode"/> 为 <see cref="ToolBarMode.Hidden"/> 时调用方直接把
    /// 返回的那张卡片 <c>IsVisible = false</c> —— 这里照样按<strong>大档</strong>把它建出来，
    /// 因为"隐藏"不该顺手把布局算成另一副样子：从隐藏切回大或小是一次赋值，
    /// 而切回哪一档由设置里那个值说，不由这里猜。
    /// </summary>
    public static Control Create(LangCatalog lang, MainToolBarActions actions, ToolBarMode mode)
    {
        var compact = mode == ToolBarMode.Small;

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

        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_OPEN", "open-archive", compact,
            MainMenuModel.CommandOpenArchive, actions.OpenArchive));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_EXTRACT", "extract", compact,
            MainMenuModel.CommandExtractArchive, actions.ExtractArchive));

        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_NEW", "new-archive", compact,
            MainMenuModel.CommandNewArchive, actions.NewArchive));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_ADD", "add-file", compact,
            MainMenuModel.CommandAddEntries, actions.AddFiles));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_DELETE", "delete-file", compact,
            MainMenuModel.CommandDeleteEntries, actions.DeleteFiles));
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_TEST", "test-archive", compact,
            MainMenuModel.CommandTestArchive, actions.TestArchive));
        // 这颗点开的是与「文件 → CRC」同一份名单（CrcItems），挑一种就算一次校验和。
        // 它带命令 id 但没有 run：亮与灰照样由按命令的那一趟管，点开的是下拉而不是一个动作。
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_CRC", "crc", compact,
            MainMenuModel.CommandCrc, run: null,
            flyout: actions.PickChecksum is { } pick ? CrcFlyout(pick) : null));
        // 「查看」就是参考软件那个预览面板，也就是「视图 → 图像预览」那一条（4.6.0 接上）。
        // 与「添加」「删除」同一个做法：这里带的是<strong>同一个命令 id</strong>，
        // 所以首页上灰、开着包才亮这两件事由按命令的那一趟统一决定，两处不会各说一套。
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_VIEW", "view", compact,
            MainMenuModel.CommandPreview, actions.TogglePreview));

        // 语言切换本身在「设置 → 语言」里（换完要重启才全面生效），
        // 所以这一颗做的是"跳到那一页"。以前它写着当前语言、点不动，如今它有地方可去。
        items.Children.Add(Cell(lang, "TEXT_TOOLBAR_LANGUAGE", "language", compact, null, actions.SwitchLanguage));

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
    /// <para><paramref name="compact"/>（小档）只画图标、不画文字，整张卡片从量的 90 DIP 降到 40 DIP
    /// —— 就是"约一半"。文字不是丢了，是挪进悬停提示（九枚图标里没有一枚是"看一眼就知道是什么"
    /// 的那种通用图形，少了名字这一排就变成一排猜谜）；读屏拿到的名字也一起补上
    /// （<c>AutomationProperties.Name</c>），否则小档对读屏用户是整排空按钮。</para>
    /// </summary>
    private static Control Cell(LangCatalog lang, string textKey, string glyph, bool compact,
        string? commandId, Action? run, MenuFlyout? flyout = null)
    {
        var label = lang.Label(textKey);
        var icon = SvgGlyph.Load(glyph, compact ? Design.IconSize.ToolBarCompact : Design.IconSize.ToolBar);

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        content.Children.Add(icon);
        if (!compact)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = Design.Type.Body,
                Foreground = Design.Ink.Primary,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, Design.Space.X2 - 1, 0, 0),
                TextAlignment = TextAlignment.Center,
            });
        }

        // 宽度交给外面那九等分的格子：这里不再写 MinWidth，写死一个下限只会在窄窗口上跟格子顶牛。
        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // 小档把上下内距收到一档（8 → 4）：图标已经小了，留着 8 的话"一半高"就只是
            // "图标小一点、条子还是那么高"，读起来不像换了档。
            Padding = new Thickness(Design.Space.X3 + 2, compact ? Design.Space.X1 : Design.Space.X2,
                Design.Space.X3 + 2, compact ? Design.Space.X1 : Design.Space.X2),
            FontFamily = Design.Font,
            Tag = commandId,
            IsEnabled = run is not null || flyout is not null,
        };

        // 带下拉的那一颗（CRC）：点一下给的是名单，不是一个动作，所以没有 run。
        if (flyout is not null)
        {
            button.Flyout = flyout;
        }

        if (compact)
        {
            ToolTip.SetTip(button, label);
            Avalonia.Automation.AutomationProperties.SetName(button, label);
        }

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
                PaintFace(button, icon, content);
            }
        };
        PaintFace(button, icon, content);

        // 悬停是一块平的淡色圆角方（3.3.0 参考图）：卡片自己已经浮起来了，格子不再各自带影。
        // 禁用时 Chrome 的事件根本不会触发（收不到指针事件），
        // 所以静止态保持透明，不会在卡片上留一块"亮着的死按钮"。
        return button.Tile();
    }

    /// <summary>
    /// 工具栏那颗 CRC 点开的下拉。与菜单里那一层子菜单共用 <see cref="CrcItems"/> ——
    /// 同一份算法名单、同一个回调，两处不会一处有 BLAKE2sp 一处没有。
    /// </summary>
    private static MenuFlyout CrcFlyout(Action<ChecksumAlgorithm> pick)
    {
        var flyout = new MenuFlyout();

        foreach (var item in CrcItems.Build(pick))
        {
            flyout.Items.Add(item);
        }

        return flyout;
    }

    /// <summary>
    /// 按 <c>IsEnabled</c> 重画这一格。文字那一段只在<strong>大档</strong>存在，
    /// 所以这里按类型找而不是记着引用 —— 小档找不到文字块是设计，不是漏。
    /// </summary>
    private static void PaintFace(Button button, Control icon, StackPanel content)
    {
        icon.Opacity = button.IsEnabled ? 1 : DisabledOpacity;

        if (content.Children.FirstOrDefault(child => child is TextBlock) is TextBlock label)
        {
            label.Foreground = button.IsEnabled ? Design.Ink.Primary : Design.Ink.Disabled;
        }

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
    Action<ChecksumAlgorithm>? PickChecksum = null,
    Action? TogglePreview = null,
    Action? SwitchLanguage = null);

