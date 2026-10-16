namespace Andzip.Core.Settings;

/// <summary>双击压缩包文件时做什么（解压设置页的第一项；1.6.0 之前住在常规页）。</summary>
public enum ArchiveDoubleClick
{
    /// <summary>打开 Andzip 主窗口浏览内容。</summary>
    OpenInAndzip,

    /// <summary>直接解压到与压缩包同名的文件夹，只显示进度条，完成后自动关闭。</summary>
    ExtractDirectly,
}

/// <summary>
/// 界面主题。默认 <see cref="System"/>：一台把系统设成深色的机器，
/// 装上一个刺眼的浅色窗口不是"保守"，是没跟上。
/// </summary>
public enum ThemeMode
{
    /// <summary>跟着系统的深浅走（系统读数拿不到时按浅色）。</summary>
    System,

    Light,

    Dark,
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

    /// <summary>压缩时预分配磁盘：先按未压缩总量占住簇，再开始写。</summary>
    public const string CompressPreallocate = "compress_preallocate";

    /// <summary>压缩时不独占源文件（别的程序可以继续写/删）。
    /// 注意这一项<strong>不能</strong>读走别人独占的文件 —— Windows 不允许，见压缩页的提示。</summary>
    public const string CompressShareSources = "compress_share_sources";

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
    /// 解压遇到「包里只有一个文件」时，要不要也建一个文件夹。
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

    /// <summary>
    /// 界面主题：浅色 / 深色 / 跟随系统。取值见 <see cref="Core.Settings.ThemeMode"/>。
    /// <para>键名不叫 <c>ThemeMode</c> —— 那会和同名的枚举撞车（<c>ThemeMode.Light</c>
    /// 在类里会被解析成"这个字符串成员的 Light"），编译器报的还是"string 没有 Light 定义"，
    /// 完全看不出根因。</para>
    /// </summary>
    public const string ViewTheme = "theme_mode";

    /// <summary>
    /// 被关掉的列表列，<c>;</c> 分隔的列标识（<c>compressed-size</c>、<c>crc</c>…，
    /// 名单只有一个来源，见 <c>ArchiveView.BuildColumns</c>）。<strong>存的是"关掉的"而不是"开着的"</strong>：
    /// 键缺失 = 一列都不关，正是引入这一项之前的界面。
    /// <para>它<strong>不在「设置 → 查看」那一页上</strong> —— 那一页的开关是"程序长什么样"
    /// （工具栏、树、主题），这一键是"那张表列出哪几栏"，两件事各有一个主人。
    /// 一个键只归一处管（1.6.0 立的规矩）：两边都能改的话，两边就都会把对方的改动盖掉。</para>
    /// </summary>
    public const string HiddenColumns = "hidden_columns";

    // ------------------------------------------------------- 查看页：图标配色
    //
    // 用户自己调的那套渐变。存字符串而不是打包成一坨 JSON：这个设置文件的规矩就是
    // 一行一个键，坏了能手工改（见本文件顶部那段）。

    public const string IconFront0 = "icon_front_0";
    public const string IconFront1 = "icon_front_1";
    public const string IconFront2 = "icon_front_2";
    public const string IconBack0 = "icon_back_0";
    public const string IconBack1 = "icon_back_1";

    /// <summary>渐变方向的下标（↘ ↓ → ↙）。</summary>
    public const string IconDirection = "icon_direction";

    /// <summary>两种字形选哪一种：<c>overflow</c> / <c>punched</c>。</summary>
    public const string IconGlyph = "icon_glyph";

    /// <summary>
    /// 这套配色<strong>是不是已经设成了程序图标</strong>。
    /// <para>为什么要单独一个键而不是"图标文件在不在"：改色时预览就要跟着变，
    /// 但只有按过那颗按钮才该去动快捷方式与任务栏。两件事分开记，
    /// 否则"我在设置里试了个颜色"会变成"桌面上那个图标悄悄换了"。</para>
    /// </summary>
    public const string IconApplied = "icon_applied";

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

    /// <summary>
    /// 把口令另外存进操作系统的凭据存储，跨重启也还在（「工具 → 密码管理器」管的就是这一层）。
    /// <para><strong>默认关</strong>：把口令留在机器上是用户要显式同意的交易，不该是装完就生效的默认值。</para>
    /// <para>这一层只写凭据，<strong>不写本文件</strong> —— 这里存的是一条路径清单（下面那个键）而不是口令。</para>
    /// </summary>
    public const string StorePasswordsInVault = "store_passwords_in_vault";

    /// <summary>
    /// 口令管理器为哪些包存过口令：<c>|</c> 分隔的完整路径。
    /// <para>只是<strong>索引</strong>（凭据的目标名是路径哈希，人读不了），口令本身不在这儿。
    /// 读写都在 <c>PasswordVaultIndex</c>，这里只放键名。</para>
    /// </summary>
    public const string PasswordVaultPaths = "password_vault_paths";

    // ---------------------------------------------------------- 最近的文件

    /// <summary>
    /// 「文件 → 最近的文件」列表，<c>|</c> 分隔的完整路径，最新在前。
    /// <para>不用 <see cref="AppSettings.GetStringList"/> 那套 <c>;</c> 分隔：
    /// 分号在 Windows 文件名里合法，会把一条记录劈成两条。<c>|</c> 才是文件名里禁止出现的字符。
    /// 读写都在 <see cref="RecentFiles"/> 里，这里只放键名。</para>
    /// </summary>
    public const string RecentFiles = "recent_files";

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

    /// <summary>认不出来回落到 <see cref="ThemeMode.System"/> —— 那是这一项首次出现时的默认。</summary>
    public static ThemeMode ParseThemeMode(string? stored)
        => stored?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>枚举存成稳定的小写标识，理由同 <see cref="ToStorage(ArchiveDoubleClick)"/>。</summary>
    public static string ToStorage(this ThemeMode mode)
        => mode switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            _ => "system",
        };

    /// <summary>
    /// 把设置折算成"到底用哪套颜色"。<paramref name="systemIsDark"/> 是系统的深浅读数。
    /// <para>单独拆出来是因为这一层判断要跟"系统读数从哪来"分开测：
    /// Avalonia 在 headless 下报什么、Windows 上改了系统主题会不会推事件，
    /// 都不是这条规则该操心的事。</para>
    /// </summary>
    public static bool PrefersDark(this ThemeMode mode, bool systemIsDark)
        => mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => systemIsDark,
        };
}
