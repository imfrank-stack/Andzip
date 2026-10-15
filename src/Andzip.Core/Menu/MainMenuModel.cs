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
/// 视图切换（F6）、关于（F1）、打开 Andzip 安装目录、转到官网。</para>
/// <para><strong>模型里 <c>Enabled</c> 的语义是"这条实装过了"</strong>，不是"这条现在能点" ——
/// 后者由窗口按当前视图现算（见 <see cref="ArchiveOnlyCommands"/>）。
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

    /// <summary>启动画面 / 压缩包视图切换（F6）。</summary>
    public const string CommandToggleStartScreen = "view.start";

    /// <summary>设置窗口（F5）。</summary>
    public const string CommandSettings = "options.settings";

    /// <summary>在当前打开的压缩包清单里找文件（Ctrl+F）。</summary>
    public const string CommandFindFiles = "find.files";

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
    /// </summary>
    public static IReadOnlyList<string> ArchiveOnlyCommands { get; } =
    [
        CommandSaveArchiveAs,
        CommandDeleteArchive,
        CommandPermanentDeleteArchive,
        CommandOpenArchiveFolder,
        CommandFindFiles,
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
            new("file.scan", "MENUSTR_SCAN_ARCHIVE", "Ctrl+I"),
            new("file.extract", "MENUSTR_EXTRACT_FILE", "Ctrl+E", Enabled: true, CommandId: CommandExtractArchive),
            MenuItemModel.Separator(),
            new("file.newwindow", "MENUSTR_NEW_WINDOW", "Ctrl+Shift+N"),
            new("file.newwindowadmin", "MENUSTR_NEW_WINDOW_ADMIN", "Ctrl+Alt+N"),
            MenuItemModel.Separator(),
            new("file.exit", "MENUSTR_EXIT", "Ctrl+W", Enabled: true, CommandId: CommandExit),
        ]),

        new("edit", "MENUSTR_EDIT",
        [
            new("edit.rename", "MENUSTR_RENAME_FILE", "F2"),
            new("edit.add", "MENUSTR_ADD_FILE", "Ins"),
            new("edit.delete", "MENUSTR_DELETE_FILE", "Del"),
            MenuItemModel.Separator(),
            new("edit.comment", "MENUSTR_EDIT_COMMENT", "Ctrl+Shift+M"),
            MenuItemModel.Separator(),
            new("edit.copy", "MENUSTR_COPY_FILES2CLIPBOARD", "Ctrl+C"),
            new("edit.paste", "MENUSTR_PASTE_FILESFROMCLIPBOARD", "Ctrl+V"),
            new("edit.copypath", "MENUSTR_COPY_FULL_PATHNAMES"),
        ]),

        new("find", "MENUSTR_FIND",
        [
            new("find.item", "MENUSTR_FIND_ITEM", "Ctrl+F", Enabled: true, CommandId: CommandFindFiles),
        ]),

        new("options", "MENUSTR_SETTING",
        [
            new("options.settings", "MENUSTR_SETTING_SUB", "F5", Enabled: true, CommandId: CommandSettings),
            MenuItemModel.Separator(),
            new("options.register", "MENUSTR_REGISTER"),
            new("options.unregister", "MENUSTR_UNREGISTER"),
            new("options.passman", "MENUSTR_PASSMAN"),
        ]),

        new("view", "MENUSTR_VIEW",
        [
            new("view.start", "MENUSTR_VIEW_STARTSCREEN", "F6", Enabled: true, CommandId: CommandToggleStartScreen),
            new("view.tree", "MENUSTR_VIEW_TREECONTROL", "F7"),
            new("view.comment", "MENUSTR_VIEW_COMMENT", "F8"),
            MenuItemModel.Separator(),
            new("view.preview", "MENUSTR_VIEW_PREVIEW"),
            new("view.statusbar", "MENUSTR_VIEW_STATUS_BAR"),
            new("view.toolbar", "MENUSTR_VIEW_TOOLBAR"),
            new("view.style", "MENUSTR_VIEW_LISTVIEW"),
            new("view.columns", "MENUSTR_VIEW_COLUMN"),
            MenuItemModel.Separator(),
            new("view.ontop", "MENUSTR_VIEW_ALWAYS_ON_TOP"),
            new("view.color", "MENUSTR_VIEW_COLOR_SETTING"),
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
            new("help.folder", "MENUSTR_OPEN_FOLDER", Enabled: true, CommandId: CommandOpenInstallFolder),
            new("help.forum", "MENUSTR_FORUM"),
            new("help.homepage", "MENUSTR_HOMEPAGE", Enabled: true, CommandId: CommandHomepage),
        ]),
    ];
}