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

    // ------------------------------------------------------------- 压缩页
    //
    // 这一组是「新建压缩文件」对话框的**默认值**来源：对话框上的控件仍可逐次改，
    // 这里只决定它每次打开时长什么样。所以它们不参与压缩本身，只在打开对话框时读一次。

    /// <summary>默认压缩级别，存 <c>CompressionLevelOption.Id</c>。</summary>
    public const string CompressLevel = "compress_level";

    /// <summary>压缩单个文件夹时不创建根目录（对应 IncludeRootFolder 取反）。</summary>
    public const string CompressNoRootFolder = "compress_no_root_folder";

    public const string CompressExcludeThumbsDb = "compress_exclude_thumbs_db";

    /// <summary>额外排除模式，原样存设置框里那串 <c>*.bak; *.tmp;</c>。</summary>
    public const string CompressExcludePatterns = "compress_exclude_patterns";

    public const string CompressTestAfter = "compress_test_after";

    /// <summary>压缩完成后保留进度窗口（关掉时跑完即消失）。</summary>
    public const string CompressKeepProgress = "compress_keep_progress";

    /// <summary>输出目录策略：沿用上次，还是固定到一个目录。</summary>
    public const string CompressOutputFolderMode = "compress_output_folder_mode";

    /// <summary><see cref="CompressOutputFolderMode"/> 选「固定」时的那个目录。</summary>
    public const string CompressOutputFolder = "compress_output_folder";

    /// <summary>
    /// 上一次成功新建压缩包所在的目录，由对话框在压完之后写回来。
    /// <para>刻意不放进 <see cref="CompressionDefaults"/>：那类型是「设置 → 压缩」页编辑并整体存盘的
    /// 一组值，而这一项是程序自己记账的结果 —— 用户在页面上看不到它，也不该被页面的「立即应用」覆盖。</para>
    /// </summary>
    public const string CompressLastOutputFolder = "compress_last_output_folder";

    /// <summary>新建压缩文件时输出目录的两种选择。</summary>
    public enum OutputFolderMode
    {
        /// <summary>上次新建时用的目录（没有则退到「文档」）。</summary>
        LastUsed,

        /// <summary>总是输出到设置里指定的那个目录。</summary>
        Fixed,
    }

    // ------------------------------------------------------------- 解压页

    /// <summary>
    /// 智能解压遇到「包里只有一个文件」时，要不要也建一个文件夹。
    /// 默认关：压一个 <c>photo.jpg.gz</c> 却得到 <c>photo.jpg\photo.jpg</c> 不是多数人想要的。
    /// </summary>
    public const string ExtractSingleFileToFolder = "extract_single_file_to_folder";

    /// <summary>解压成功后用资源管理器打开目标目录。</summary>
    public const string ExtractOpenFolderAfter = "extract_open_folder_after";

    /// <summary>
    /// 主窗口里的「解压文件」完成后保留进度窗口。
    /// <para>只管这一条路径：右键与双击那条"跑完即消失"是更早明确定下的行为，别合并。</para>
    /// </summary>
    public const string ExtractKeepProgress = "extract_keep_progress_window";

    // ------------------------------------------------------------- 查看页
    //
    // 这一组影响的是"界面骨架露哪几块"，MainWindow 在设置对话框关掉后会重新应用一次，
    // 所以改完立刻见效，不用重启。

    public const string ViewShowToolBar = "view_show_toolbar";
    public const string ViewShowStatusBar = "view_show_status_bar";
    public const string ViewShowTree = "view_show_tree";

    /// <summary>打开压缩包时先用图标视图而不是详情列表。</summary>
    public const string ViewIconByDefault = "view_icon_by_default";

    /// <summary>树形目录里的文件夹是否预先展开。</summary>
    public const string ViewAutoExpandTree = "view_auto_expand_tree";

    // ------------------------------------------------------------- 语言页

    /// <summary>
    /// 界面语言（<c>SimpChinese</c> / <c>English</c>，取语言包文件名）。
    /// <para>换语言要重启才全面生效：菜单、工具栏、标题栏这些文案是在构造时从语言包取一次
    /// 就固化到控件上的，翻 <c>LangCatalog</c> 只会影响之后新建的界面。这一点在界面上要如实告诉用户。</para>
    /// </summary>
    public const string Language = "language";

    // ---------------------------------------------------------- 高级 / 安全页

    /// <summary>标题栏显示压缩包的完整路径而不只是文件名。</summary>
    public const string TitleBarFullPath = "title_bar_full_path";

    /// <summary>
    /// 拼接 WinZip 风格分卷（<c>name.z01 + name.zip</c>）时用的临时目录；空表示系统临时目录。
    /// <para>只有这一处会落地临时文件 —— 7z 风格的分卷由引擎自己取兄弟卷，不经过磁盘。</para>
    /// </summary>
    public const string TempDirectory = "temp_directory";

    /// <summary>同一次运行里记住用过的口令，不再对同一个包重复追问。</summary>
    public const string RememberPasswordsThisSession = "remember_passwords_this_session";

    /// <summary>认不出来回落到 <see cref="OutputFolderMode.LastUsed"/> —— 那是改动前的行为。</summary>
    public static OutputFolderMode ParseOutputFolderMode(string? stored)
        => stored?.Trim().ToLowerInvariant() == "fixed" ? OutputFolderMode.Fixed : OutputFolderMode.LastUsed;

    /// <summary>枚举存成稳定的小写标识，理由同 <see cref="ToStorage(ArchiveDoubleClick)"/>。</summary>
    public static string ToStorage(this OutputFolderMode mode)
        => mode == OutputFolderMode.Fixed ? "fixed" : "last";

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
