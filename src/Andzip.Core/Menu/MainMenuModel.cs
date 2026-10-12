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
/// 当前已接入命令：退出（Ctrl+W）、新建压缩文件（Ctrl+N）、打开压缩文件（Ctrl+O）、
/// 关闭（F4）、解压文件、测试压缩文件（Ctrl+T）；其余项按参考软件结构占位（禁用态），后续里程碑逐项接入。
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

    /// <summary>启动画面 / 压缩包视图切换（F6）。</summary>
    public const string CommandToggleStartScreen = "view.start";

    /// <summary>设置窗口（F5）。</summary>
    public const string CommandSettings = "options.settings";

    public static IReadOnlyList<MenuModel> Create() =>
    [
        new("file", "MENUSTR_FILE",
        [
            new("file.open", "MENUSTR_OPEN", "Ctrl+O", Enabled: true, CommandId: CommandOpenArchive),
            new("file.recent", "MENUSTR_RECENT"),
            MenuItemModel.Separator(),
            new("file.new", "MENUSTR_CREATE_ARCHIVE", "Ctrl+N", Enabled: true, CommandId: CommandNewArchive),
            new("file.close", "MENUSTR_CLOSE_FILE", "F4", Enabled: true, CommandId: CommandCloseArchive),
            new("file.saveas", "MENUSTR_SAVE_AS_ARCHIVE"),
            MenuItemModel.Separator(),
            new("file.delete", "MENUSTR_DELETE_ARCHIVE", "Ctrl+Del"),
            new("file.permanentdelete", "MENUSTR_PERM_DELETE_ARCHIVE", "Shift+Ctrl+Del"),
            new("file.openfolder", "MENUSTR_OPEN_ARCHIVE_FOLDER"),
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
            new("find.item", "MENUSTR_FIND_ITEM", "Ctrl+F"),
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
            new("tools.recovery", "MENUSTR_TOOLS_RECOVERY"),
        ]),

        new("help", "MENUSTR_HELP",
        [
            new("help.about", "MENUSTR_ABOUT", "F1"),
            MenuItemModel.Separator(),
            new("help.page", "MENUSTR_HELPPAGE"),
            new("help.folder", "MENUSTR_OPEN_FOLDER"),
            new("help.forum", "MENUSTR_FORUM"),
            new("help.homepage", "MENUSTR_HOMEPAGE"),
        ]),
    ];
}