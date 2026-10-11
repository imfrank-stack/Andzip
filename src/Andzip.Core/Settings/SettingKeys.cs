namespace Andzip.Core.Settings;

/// <summary>双击压缩包文件时做什么（常规设置页的第一项）。</summary>
public enum ArchiveDoubleClick
{
    /// <summary>打开 Andzip 主窗口浏览内容。</summary>
    OpenInAndzip,

    /// <summary>直接解压到与压缩包同名的文件夹，只显示进度条，完成后自动关闭。</summary>
    ExtractDirectly,
}

/// <summary>
/// 设置键名与取值解析。集中一处，免得键名在各处拼字符串拼歪。
/// </summary>
public static class SettingKeys
{
    public const string DoubleClickAction = "double_click_action";
    public const string AllowEscCancelProgress = "allow_esc_cancel_progress";

    /// <summary>
    /// 右键「解压到…」两项要不要写进 ProgID。
    /// <para>
    /// 这一项是<strong>策略</strong>而不是现状的快照：解压动词住在每个 <c>Andzip.*</c> ProgID 里，
    /// 而ProgID 会随新关联不断增多，所以必须有个地方记住「用户要不要它们有」。设置文件里没这一项时
    /// 按开启处理 —— 那是这个功能首次出现的那个版本该有的默认。
    /// </para>
    /// </summary>
    public const string ContextMenuExtractVerbs = "context_menu_extract_verbs";

    /// <summary>枚举存成稳定的小写标识（不是数字），这样以后插入新值不会让老设置错位。</summary>
    public static string ToStorage(this ArchiveDoubleClick value)
        => value == ArchiveDoubleClick.ExtractDirectly ? "extract" : "open";

    /// <summary>认不出来的取值（手改过、或来自更新版本）回落到 <see cref="ArchiveDoubleClick.OpenInAndzip"/>。</summary>
    public static ArchiveDoubleClick ParseDoubleClick(string? stored)
        => stored?.Trim().ToLowerInvariant() == "extract"
            ? ArchiveDoubleClick.ExtractDirectly
            : ArchiveDoubleClick.OpenInAndzip;

    /// <summary>
    /// 双击（命令行里没有任何任务开关时）要不要「直接解压」而不是打开主窗口。
    /// <para>
    /// 行为由<strong>设置</strong>决定，而不是由注册表里哪个动词被调用决定 —— 后者踩过坑：
    /// 把 <c>shell</c> 默认值改成 <c>extract</c> 之后，老版本注册出来的 ProgID 里根本没有
    /// <c>shell\extract\command</c>，Windows 找不到动词就退回 <c>open</c>，表现成「设了没用」。
    /// 而且绿色版的文件名带版本号，升级后注册表里那条命令还指着上一个 exe。
    /// </para>
    /// <para>右键菜单不走这里：它的命令行由 <c>ShellTasks</c> 写明了做什么。</para>
    /// </summary>
    public static bool ShouldExtractDirectly(string? storedSetting)
        => ParseDoubleClick(storedSetting) == ArchiveDoubleClick.ExtractDirectly;
}
