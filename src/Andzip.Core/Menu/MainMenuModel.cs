namespace Andzip.Core.Menu;

/// <summary>菜单项模型。TextKey 为语言包键名（与参考软件键名体系对齐），Shortcut 为展示用快捷键文案。</summary>
public sealed record MenuItemModel(
    string Id,
    string TextKey,
    string? Shortcut = null,
    bool IsSeparator = false,
    bool Enabled = false,
    string? CommandId = null)
{
    public static MenuItemModel Separator() => new("sep", string.Empty, IsSeparator: true);

    public ShortcutSpec? Gesture => IsSeparator ? null : ShortcutSpec.Parse(Shortcut);
}

/// <summary>顶级菜单模型。</summary>
public sealed record MenuModel(string Id, string TextKey, IReadOnlyList<MenuItemModel> Items);

/// <summary>
/// 主菜单结构（对应需求文档附录 A，7 个顶级菜单）。
/// <para>已接入的命令：退出（Ctrl+W）、新建压缩文件（Ctrl+N）、打开压缩文件（Ctrl+O）、
/// 关闭（F4）、解压文件、测试压缩文件（Ctrl+T）、文件查找（Ctrl+F）、设置（F5）、
/// 关于（F1）、打开 Andzip 安装目录、转到官网，
/// 以及「编辑」整排七项（重命名 / 添加 / 删除 / 注释 / 复制 / 粘贴 / 复制完整路径，3.4.0）、
/// 「视图 → 列设置」（4.1.0，勾一列当场少一列，选择记进 <c>hidden_columns</c>）。</para>
/// <para><strong>模型里 <c>Enabled</c> 的语义是"这条实装过了"</strong>，不是"这条现在能点" ——
/// 后者由窗口按当前视图与选择现算（见 <see cref="ArchiveOnlyCommands"/> 与
/// <see cref="SelectionCommands"/>）。
/// 剩下那些 <c>Enabled: false</c> 的是按参考软件结构留的占位，点了什么也不会发生，
/// 所以它们必须灰着，而不是接一条假命令。</para>
/// </summary>
public static class MainMenuModel
{
    public const string CommandExit = "app.exit";

    public const string CommandNewArchive = "file.new";

    /// <summary>打开压缩文件（M3 起打开压缩包视图）。</summary>
    public const string CommandOpenArchive = "file.open";

    /// <summary>关闭压缩文件（回到启动画面）。</summary>
    public const string CommandCloseArchive = "file.close";

    /// <summary>解压文件（未打开压缩包时先选择压缩包）。</summary>
    public const string CommandExtractArchive = "file.extract";

    /// <summary>测试压缩包完整性（Ctrl+T；未打开时同样先选择压缩包）。</summary>
    public const string CommandTestArchive = "file.test";

    /// <summary>
    /// 扫描压缩包里的恶意软件（Ctrl+I）。<strong>Andzip 自己没有病毒库，也不上传任何文件</strong>：
    /// 这一条做的只是"把这个文件交给这台机器上已经注册的反恶意软件引擎（AMSI）判断一次"。
    /// 引擎不在、或它管不了这类内容，界面上就照实说。
    /// </summary>
    public const string CommandScanArchive = "file.scan";

    /// <summary>另起一个 Andzip 进程，开一扇新窗口（Ctrl+Shift+N）。当前开着包就一起带过去。</summary>
    public const string CommandNewWindow = "file.newwindow";

    /// <summary>
    /// 同上，但那一扇是<strong>提权之后</strong>启动的（Ctrl+Alt+N）。
    /// <para>提权框由系统弹、由用户按 —— 这一条存在的意义就是"用户明确要一次管理员窗口"，
    /// 其余任何路径都不许顺手提权（红线：绝不静默提权）。</para>
    /// </summary>
    public const string CommandNewWindowAdmin = "file.newwindowadmin";

    /// <summary>修复损坏的 ZIP（三种模式由用户选；只写副本，绝不动原包）。</summary>
    public const string CommandRepairArchive = "tools.repair";

    /// <summary>口令恢复（<c>工具 → 密码恢复</c>）：忘了自己设的口令时，把候选口令一个一个真试一遍。</summary>
    public const string CommandRecoverPassword = "tools.recovery";

    /// <summary>把当前打开的压缩包另存一份到别处（<strong>复制</strong>，不重压）。</summary>
    public const string CommandSaveArchiveAs = "file.saveas";

    /// <summary>把当前压缩包移到回收站（还能反悔）。</summary>
    public const string CommandDeleteArchive = "file.delete";

    /// <summary>直接删掉当前压缩包，不经过回收站。</summary>
    public const string CommandPermanentDeleteArchive = "file.permanentdelete";

    /// <summary>在资源管理器里打开当前压缩包所在目录并选中它。</summary>
    public const string CommandOpenArchiveFolder = "file.openfolder";

    /// <summary>
    /// 「视图 → 列设置」。<strong>这一条不是一条命令，而是一个子菜单的挂载点</strong>：
    /// 点它没有动作，展开的是"每一列勾着 / 不勾"那一排（与「最近的文件」同一种形状，
    /// 所以它同样要登记进主窗口那份"命令在别处接走"的名单里，不然构造时就抛）。
    /// </summary>
    public const string CommandColumns = "view.columns";

    /// <summary>
    /// 「视图 → 图像预览」：勾上就把选中那一张图解在左栏底下那一格里。
    /// <para>它挂的是<strong>压缩包视图</strong>那一格版面，所以与 <see cref="CommandColumns"/>
    /// 一样在"只有开着包才能用"那张表里：首页上没有那一格，勾一下屏幕上不会有任何变化，
    /// 而"看得见、点了没反应"正是这一排从 0.9.x 起就在挡的形状。</para>
    /// </summary>
    public const string CommandPreview = "view.preview";

    /// <summary>
    /// 「视图 → 工具栏」：大 / 小 / 隐藏 三档。与 <see cref="CommandColumns"/> 同一种形状 ——
    /// <strong>不是一条命令，而是一个子菜单的挂载点</strong>，所以也要登记进主窗口那份
    /// "命令在别处接走"的名单里，不然构造时就抛。
    /// </summary>
    public const string CommandToolBar = "view.toolbar";

    /// <summary>
    /// 深色模式开关。它做的事与「设置 → 查看 → 界面主题」里那一档是同一件，
    /// 只是<strong>给一个看得见的位置</strong> —— 那个下拉住在九页设置的第五页里，
    /// 用户找不到它，于是"夜间模式"这个已经做完的功能在界面上等于不存在
    /// （与 1.9.2 那句「没有卸载 uninstall」同一族：缺的从来不是功能，是入口）。
    /// </summary>
    public const string CommandToggleTheme = "view.theme";

    /// <summary>
    /// 「视图 → 置顶」：把这扇窗口钉在最前面，再点一次放开。
    /// <para>勾是<strong>这一扇窗口的状态</strong>，不写进 <c>settings.ini</c>：置顶是一次
    /// "我先盯着这个窗口办点事"的临时姿势，把它做成跨重启的记忆，就等于每次开机都有一扇
    /// 挡在别的窗口前面的 Andzip —— 而它下面那一排（深浅、列设置、工具栏）都是会落盘的设置，
    /// 只有这一条不是，所以它的勾只能由窗口自己回答。</para>
    /// </summary>
    public const string CommandToggleOnTop = "view.ontop";

    /// <summary>设置窗口（F5）。</summary>
    public const string CommandSettings = "options.settings";

    /// <summary>在当前打开的压缩包清单里找文件（Ctrl+F）。</summary>
    public const string CommandFindFiles = "find.files";

    /// <summary>「编辑」那一排：重命名一个成员（F2）。要恰好选中一个文件。</summary>
    public const string CommandRenameEntry = "edit.rename";

    /// <summary>「编辑」：把磁盘上的文件加进这个包（Ins）。</summary>
    public const string CommandAddEntries = "edit.add";

    /// <summary>「编辑」：把选中的成员从包里删掉（Del）。</summary>
    public const string CommandDeleteEntries = "edit.delete";

    /// <summary>「编辑」：改这个包自己的注释（Ctrl+Shift+M；只有 ZIP 有这一格）。</summary>
    public const string CommandEditComment = "edit.comment";

    /// <summary>「编辑」：把选中的成员复制到剪贴板（Ctrl+C；落到临时目录再上剪贴板）。</summary>
    public const string CommandCopyEntries = "edit.copy";

    /// <summary>「编辑」：把剪贴板里的文件加进这个包（Ctrl+V）。</summary>
    public const string CommandPasteEntries = "edit.paste";

    /// <summary>「编辑」：把选中成员的完整路径复制成文本。</summary>
    public const string CommandCopyPaths = "edit.copypath";

    /// <summary>
    /// 「最近的文件」子菜单。它的条目是运行时按存储列表生成的，所以这里只登记"这一项有子菜单"，
    /// 具体内容由 <c>MainMenuBar</c> 在展开时填 —— 菜单建一次就冻住的话，
    /// 用户打开十个包之后看到的还是空的。
    /// </summary>
    public const string CommandRecentFiles = "file.recent";

    /// <summary>关于窗口（F1）：版本号、压缩内核、许可证、项目主页。</summary>
    public const string CommandAbout = "help.about";

    /// <summary>在资源管理器里打开 Andzip 自己所在的那个目录（绿色版与安装版都答得准）。</summary>
    public const string CommandOpenInstallFolder = "help.folder";

    /// <summary>用系统默认浏览器打开项目主页。</summary>
    public const string CommandHomepage = "help.homepage";

    /// <summary>
    /// 只有"正开着某个压缩包"才能用的命令。模型里给 <c>Enabled: true</c>（表示这些是实装过的，
    /// 不是占位），窗口按当前视图把不符合条件的那几条真的置灰 ——
    /// 首页上摆一条能点、点了什么也没有的「删除压缩文件」比灰着更糟。
    /// <para>「列设置」在这一组里是因为它<strong>改的是那张表的长相</strong>：首页上没有表，
    /// 勾一下什么也不会发生（选择照样落盘，下一次开包生效）。让一条看得见、点下去没反应的
    /// 菜单去"教"用户这个软件还有列可以挑，不如等那张表真的在屏幕上时再给这个入口。</para>
    /// </summary>
    public static IReadOnlyList<string> ArchiveOnlyCommands { get; } =
    [
        CommandSaveArchiveAs,
        CommandDeleteArchive,
        CommandPermanentDeleteArchive,
        CommandOpenArchiveFolder,
        CommandFindFiles,
        CommandColumns,
        CommandPreview,
    ];

    /// <summary>
    /// 「编辑」那七项。逐项判的条件比上面那组细：改得动这个格式才点亮增删改名与粘贴、
    /// 注释只对 ZIP、复制类只要选中了东西。
    /// <para>这张表只列"归窗口按选择现算的那几项"，好让窗口知道该管谁 ——
    /// 条件本身写在窗口里（<c>UpdateEditCommandStates</c>），因为只有它看得见当前选择。</para>
    /// </summary>
    public static IReadOnlyList<string> SelectionCommands { get; } =
    [
        CommandRenameEntry,
        CommandAddEntries,
        CommandDeleteEntries,
        CommandEditComment,
        CommandCopyEntries,
        CommandPasteEntries,
        CommandCopyPaths,
    ];

    public static IReadOnlyList<MenuModel> Create() =>
    [
        new("file", "MENUSTR_FILE",
        [
            new("file.open", "MENUSTR_OPEN", "Ctrl+O", Enabled: true, CommandId: CommandOpenArchive),
            new("file.recent", "MENUSTR_RECENT", Enabled: true, CommandId: CommandRecentFiles),
            MenuItemModel.Separator(),
            new("file.new", "MENUSTR_CREATE_ARCHIVE", "Ctrl+N", Enabled: true, CommandId: CommandNewArchive),
            new("file.close", "MENUSTR_CLOSE_FILE", "F4", Enabled: true, CommandId: CommandCloseArchive),
            new("file.saveas", "MENUSTR_SAVE_AS_ARCHIVE", Enabled: true, CommandId: CommandSaveArchiveAs),
            MenuItemModel.Separator(),
            new("file.delete", "MENUSTR_DELETE_ARCHIVE", "Ctrl+Del", Enabled: true, CommandId: CommandDeleteArchive),
            new("file.permanentdelete", "MENUSTR_PERM_DELETE_ARCHIVE", "Shift+Ctrl+Del", Enabled: true, CommandId: CommandPermanentDeleteArchive),
            new("file.openfolder", "MENUSTR_OPEN_ARCHIVE_FOLDER", Enabled: true, CommandId: CommandOpenArchiveFolder),
            MenuItemModel.Separator(),
            new("file.test", "MENUSTR_TEST_ARCHIVE", "Ctrl+T", Enabled: true, CommandId: CommandTestArchive),
            new("file.scan", "MENUSTR_SCAN_ARCHIVE", "Ctrl+I", Enabled: true, CommandId: CommandScanArchive),
            new("file.extract", "MENUSTR_EXTRACT_FILE", "Ctrl+E", Enabled: true, CommandId: CommandExtractArchive),
            MenuItemModel.Separator(),
            new("file.newwindow", "MENUSTR_NEW_WINDOW", "Ctrl+Shift+N", Enabled: true, CommandId: CommandNewWindow),
            new("file.newwindowadmin", "MENUSTR_NEW_WINDOW_ADMIN", "Ctrl+Alt+N", Enabled: true, CommandId: CommandNewWindowAdmin),
            MenuItemModel.Separator(),
            new("file.exit", "MENUSTR_EXIT", "Ctrl+W", Enabled: true, CommandId: CommandExit),
        ]),

        new("edit", "MENUSTR_EDIT",
        [
            new("edit.rename", "MENUSTR_RENAME_FILE", "F2", Enabled: true, CommandId: CommandRenameEntry),
            new("edit.add", "MENUSTR_ADD_FILE", "Ins", Enabled: true, CommandId: CommandAddEntries),
            new("edit.delete", "MENUSTR_DELETE_FILE", "Del", Enabled: true, CommandId: CommandDeleteEntries),
            MenuItemModel.Separator(),
            new("edit.comment", "MENUSTR_EDIT_COMMENT", "Ctrl+Shift+M", Enabled: true, CommandId: CommandEditComment),
            MenuItemModel.Separator(),
            new("edit.copy", "MENUSTR_COPY_FILES2CLIPBOARD", "Ctrl+C", Enabled: true, CommandId: CommandCopyEntries),
            new("edit.paste", "MENUSTR_PASTE_FILESFROMCLIPBOARD", "Ctrl+V", Enabled: true, CommandId: CommandPasteEntries),
            new("edit.copypath", "MENUSTR_COPY_FULL_PATHNAMES", Enabled: true, CommandId: CommandCopyPaths),
        ]),

        new("find", "MENUSTR_FIND",
        [
            new("find.item", "MENUSTR_FIND_ITEM", "Ctrl+F", Enabled: true, CommandId: CommandFindFiles),
        ]),

        new("options", "MENUSTR_SETTING",
        [
            new("options.settings", "MENUSTR_SETTING_SUB", "F5", Enabled: true, CommandId: CommandSettings),
            // 「注册 Andzip」「取消注册 Andzip」这两条在 3.0.0 删了：它们从 0.9.x 起就是
            // 没有接线的灰占位，真正的注册与撤销在「设置 → 文件关联」和「设置 → 上下文菜单」
            // 那两页上，各自都有「立即应用」。摆两条点了什么也不发生的菜单，
            // 只是让人以为这功能缺着（见开发记录第七节第 60 条那条不变式）。
            // 「密码管理器」4.3.0 跟着它背后那层存储一起删了 —— 这一页现在只有「设置」一条，
            // 所以这一组不再需要分隔线。
        ]),

        new("view", "MENUSTR_VIEW",
        [
            // 「启动画面」（F6 在首页与压缩包视图之间来回）4.3.0 删了：来回这两面各有各的入口
            // ——「文件 → 关闭压缩文件」回首页，打开一个包进压缩包视图，这一条是第三扇门。
            // 「状态栏」「颜色设置」也一并删：前一条与「设置 → 查看」里那个真正的开关
            // 重复（而且这一条还是灰的），后一条对应的功能压根不存在。
            // 「置顶」4.3.0 删的是那条<strong>灰占位</strong>，4.5.0 把它作为真功能放回来了。
            new("view.tree", "MENUSTR_VIEW_TREECONTROL", "F7"),
            new("view.comment", "MENUSTR_VIEW_COMMENT", "F8"),
            MenuItemModel.Separator(),
            new("view.preview", "MENUSTR_VIEW_PREVIEW", Enabled: true, CommandId: CommandPreview),
            new("view.toolbar", "MENUSTR_VIEW_TOOLBAR", Enabled: true, CommandId: CommandToolBar),
            new("view.style", "MENUSTR_VIEW_LISTVIEW"),
            new("view.columns", "MENUSTR_VIEW_COLUMN", Enabled: true, CommandId: CommandColumns),
            MenuItemModel.Separator(),
            new("view.ontop", "MENUSTR_VIEW_ALWAYS_ON_TOP", Enabled: true, CommandId: CommandToggleOnTop),
            new("view.theme", "MENUSTR_VIEW_THEME", Enabled: true, CommandId: CommandToggleTheme),
        ]),

        new("tools", "MENUSTR_TOOLS",
        [
            new("tools.repair", "MENUSTR_TOOLS_REPAIR", Enabled: true, CommandId: CommandRepairArchive),
            new("tools.recovery", "MENUSTR_TOOLS_RECOVERY", Enabled: true, CommandId: CommandRecoverPassword),
        ]),

        new("help", "MENUSTR_HELP",
        [
            new("help.about", "MENUSTR_ABOUT", "F1", Enabled: true, CommandId: CommandAbout),
            MenuItemModel.Separator(),
            // 「转到帮助页面」删掉了（2.8.0）：它和「转到官网」是同一个目标换两个名字，
            // 而项目现在只有一个对外地址 —— 与其摆两条让人猜哪条是真的，不如只留一条。
            // 「转到用户论坛」4.3.0 也删了：它从挂上来那天就是灰的，因为没有那个地址。
            new("help.folder", "MENUSTR_OPEN_FOLDER", Enabled: true, CommandId: CommandOpenInstallFolder),
            new("help.homepage", "MENUSTR_HOMEPAGE", Enabled: true, CommandId: CommandHomepage),
        ]),
    ];
}