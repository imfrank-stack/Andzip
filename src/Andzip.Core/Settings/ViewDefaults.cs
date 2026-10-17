namespace Andzip.Core.Settings;

/// <summary>
/// 「设置 → 查看」那一页的默认值：界面骨架该露哪几块。
/// <para>
/// 这一页与压缩/解压页有一点不同 —— 它影响的是<strong>已经开着的窗口</strong>，
/// 所以 <c>MainWindow</c> 在设置对话框关掉之后会重新应用一次（见 <c>ApplyViewDefaults</c>），
/// 不必重启。语言页就没这么幸运，那是另一回事。
/// </para>
/// <para>
/// <strong>这里没有工具栏那一档</strong>：4.4.0 起它归「视图 → 工具栏」那个二级菜单管
/// （<c>SettingKeys.ViewToolBar</c>），与「视图 → 列设置」同一条理由 ——
/// 一个键只归一处管，两边都能改的话两边就都会把对方的改动盖掉。
/// 这一页原来那个「显示工具栏」开关因此整行删掉了。
/// </para>
/// <para>
/// <strong>4.7.0 起也没有"要不要那棵树"</strong>：左栏那一块是目录导航，也是选中与
/// 图像预览住的地方，藏掉它没有第二种用法 —— 原来那一档（<c>view_show_tree</c>）与
/// 「视图 → 树形目录」那行灰占位一起删了。树本身还在，<see cref="AutoExpandTree"/>
/// 管的还是它打开时摊不摊平。
/// </para>
/// </summary>
public sealed record ViewDefaults(
    bool ShowStatusBar,
    bool IconViewByDefault,
    bool AutoExpandTree,

    /// <summary>有默认值是有意的：主题这一维是后加的，让既有的构造点（含测试）不必全部改一遍。
    /// 设置页永远显式传值，所以这个默认只在"没提到主题"的调用点上生效。</summary>
    ThemeMode Theme = ThemeMode.System)
{
    /// <summary>
    /// 缺省值 = 引入这一页之前的界面：骨架全都在、默认是详情列表。
    /// <para>两个刻意的例外：<strong>树默认不展开</strong>（2026-10-13 改的）——
    /// 以前一打开就把整棵目录摊平，深包的树长得比窗口还高，第一屏看到的全是文件夹名而不是内容；
    /// 以及<strong>主题默认跟随系统</strong> —— 一台把系统设成深色的机器，
    /// 装上一个刺眼的浅色窗口不是保守，是没跟上。</para>
    /// </summary>
    public static ViewDefaults Fallback { get; } = new(
        ShowStatusBar: true,
        IconViewByDefault: false,
        AutoExpandTree: false,
        Theme: ThemeMode.System);

    public static ViewDefaults Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ViewDefaults(
            settings.GetBool(SettingKeys.ViewShowStatusBar, fallback: true),
            settings.GetBool(SettingKeys.ViewIconByDefault, fallback: false),
            settings.GetBool(SettingKeys.ViewAutoExpandTree, fallback: false),
            SettingKeys.ParseThemeMode(settings.GetString(SettingKeys.ViewTheme)));
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetBool(SettingKeys.ViewShowStatusBar, ShowStatusBar);
        settings.SetBool(SettingKeys.ViewIconByDefault, IconViewByDefault);
        settings.SetBool(SettingKeys.ViewAutoExpandTree, AutoExpandTree);
        settings.SetString(SettingKeys.ViewTheme, Theme.ToStorage());
    }
}
