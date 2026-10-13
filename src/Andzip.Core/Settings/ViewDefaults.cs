namespace Andzip.Core.Settings;

/// <summary>
/// 「设置 → 查看」那一页的默认值：界面骨架该露哪几块。
/// <para>
/// 这一页与压缩/解压页有一点不同 —— 它影响的是<strong>已经开着的窗口</strong>，
/// 所以 <c>MainWindow</c> 在设置对话框关掉之后会重新应用一次（见 <c>ApplyViewDefaults</c>），
/// 不必重启。语言页就没这么幸运，那是另一回事。
/// </para>
/// </summary>
public sealed record ViewDefaults(
    bool ShowToolBar,
    bool ShowStatusBar,
    bool ShowTree,
    bool IconViewByDefault,
    bool AutoExpandTree)
{
    /// <summary>
    /// 缺省值 = 引入这一页之前的界面：骨架全都在、默认是详情列表。
    /// <para>唯一的例外是<strong>树默认不展开</strong>（2026-10-13 改的）：以前一打开就把整棵
    /// 目录摊平，深包的树长得比窗口还高，第一屏看到的全是文件夹名而不是内容。</para>
    /// </summary>
    public static ViewDefaults Fallback { get; } = new(
        ShowToolBar: true,
        ShowStatusBar: true,
        ShowTree: true,
        IconViewByDefault: false,
        AutoExpandTree: false);

    public static ViewDefaults Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ViewDefaults(
            settings.GetBool(SettingKeys.ViewShowToolBar, fallback: true),
            settings.GetBool(SettingKeys.ViewShowStatusBar, fallback: true),
            settings.GetBool(SettingKeys.ViewShowTree, fallback: true),
            settings.GetBool(SettingKeys.ViewIconByDefault, fallback: false),
            settings.GetBool(SettingKeys.ViewAutoExpandTree, fallback: false));
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetBool(SettingKeys.ViewShowToolBar, ShowToolBar);
        settings.SetBool(SettingKeys.ViewShowStatusBar, ShowStatusBar);
        settings.SetBool(SettingKeys.ViewShowTree, ShowTree);
        settings.SetBool(SettingKeys.ViewIconByDefault, IconViewByDefault);
        settings.SetBool(SettingKeys.ViewAutoExpandTree, AutoExpandTree);
    }
}
