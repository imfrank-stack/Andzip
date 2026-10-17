using System.ComponentModel;
using System.Diagnostics;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Andzip.App;

/// <summary>
/// 主窗口：菜单栏 + 蓝色工具栏（FR-1）+ 内容区（首页 / 压缩包视图）。
/// <para>
/// 首页与压缩包视图共用同一套双栏骨架（见 <see cref="HomeView"/> / <see cref="ArchiveView"/>），
/// 所以打开与关闭压缩包不会让窗口布局跳动。
/// </para>
/// <para>快捷键由菜单模型生成（<see cref="MainMenuModel"/>），工具栏与菜单共用同一批命令委托。</para>
/// </summary>
public sealed class MainWindow : Window
{
    /// <summary>
    /// 标题里的自己：名字 + <strong>版本号</strong>。
    /// <para>为什么把版本号写进标题：连着几条"按下变黑"的反馈里，有截图量的是旧构建（1.8.7）的状态，
    /// 而界面上没有一处写着版本 —— 谁也没法从一张图判断它是谁。多这一段，每张截图自己报户口。
    /// 屏幕上的那一截在面板右下角（3.1.0 起），拼法只有一个来源，见 <see cref="AppIdentity"/>。</para>
    /// </summary>
    private static string BaseTitle { get; } = $"Andzip (Professional) {AppIdentity.Version}";

    /// <summary>
    /// 标题里带上压缩内核信息：一眼能确认 native 引擎真的加载了，而不是静默降级。
    /// 引擎缺失时这里会显示 "compression engine unavailable"。
    /// </summary>
    private static string EngineTitle => $"{BaseTitle} — {ArchiveEngine.Describe()}";

    /// <summary>
    /// 标题里那个包名。「设置 → 高级 → 标题栏显示完整路径」决定是一整条路径还是只有文件名。
    /// <para>集中成一个方法而不是在三处各拼一遍：三处写法一旦分叉，切视图时标题会突然变样。</para>
    /// </summary>
    private string ArchiveTitle(string archivePath)
        => $"{(_titleShowsFullPath ? archivePath : Path.GetFileName(archivePath))} - {BaseTitle}";

    private readonly LangCatalog _lang;
    private readonly ContentControl _contentHost = new();

    /// <summary>首页那份内容。换主题时要重做一份 —— 它的底色是构造时固化上去的。</summary>
    private Control _homeContent;

    /// <summary>
    /// 工具栏。它<strong>不是</strong>只重画底色就行的那种控件：按钮的图标与文字颜色是在
    /// <see cref="MainToolBar.Create"/> 里按当时的调色板取死的，换主题必须整条重做一份
    /// （深色下"打开/解压/新建"的文字消失过，就是因为只刷了卡片没重做这里）。
    /// </summary>
    private Control _toolBar;

    /// <summary>菜单条。留着是为了按命令找到某几项、在没有包可操作时把它们置灰。</summary>
    private readonly Menu _menuBar;

    /// <summary>
    /// 命令 id → 做什么。<strong>菜单与工具栏读的都是这一张表</strong>：
    /// 「编辑 → 添加文件」与工具栏那颗「添加」是同一条命令的两种入口，
    /// 各写一份的话，早晚会出现"菜单里能点、工具栏上那颗点了没反应"（或反过来）。
    /// 置灰也按这里的 id 走（见 <see cref="SetCommandEnabled"/>）。
    /// </summary>
    private readonly Dictionary<string, Action> _commands;

    /// <summary>自绘标题栏（图标 + 菜单 + 版本号 + 三个窗口按钮，一行）。换主题时要重取一遍令牌。</summary>
    private readonly TitleBar _titleBar;

    /// <summary>整块窗口那张圆角阴影面板。它的底色就是"表面"，最大化时收掉圆角与边距。</summary>
    private readonly Border _surface;

    /// <summary>工具栏外面那层：只给一圈留白（卡片影子的地方），不画线。</summary>
    private readonly Border _headerHost;
    private ArchiveView? _browser;
    private ArchiveListing? _listing;

    /// <summary>当下这个压缩包视图（没开着包时为 null）。
    /// <para>给界面用例用：'选了某一行之后某一项该不该亮'只能真的去选那一行才问得出来，
    /// 而选择是视图内部的状态。生产代码里没有人读它 —— 窗口自己走 <c>_browser</c>。</para></summary>
    internal ArchiveView? Browser => _browser;
    /// <summary>
    /// 已启用、但<strong>不归主窗口命令表管</strong>的那几条。「最近的文件」的条目是展开时现生成的
    /// （见 <c>MainMenuBar</c>），它没有"点一下这条菜单"这个动作；「列设置」「工具栏」「CRC」
    /// 同理 —— 点它们不是执行一件事，是展开一层子菜单。
    /// </summary>
    private static readonly IReadOnlyList<string> HandledElsewhere =
        [MainMenuModel.CommandRecentFiles, MainMenuModel.CommandColumns, MainMenuModel.CommandToolBar,
            MainMenuModel.CommandCrc];

    private FindWindow? _find;
    private AboutWindow? _about;
    private PasswordRecoveryWindow? _recovery;

    private readonly AppSettings _settings;

    /// <summary>「设置 → 查看」的当前值；设置对话框关掉之后会重新读一次并应用。</summary>
    private ViewDefaults _view;

    /// <summary>「查看 → 深色模式」切回深色之前那一档时用的那一份记忆（没进过深色就是它当前的值）。</summary>
    private ThemeMode _beforeDark = ThemeMode.System;

    /// <summary>「设置 → 高级 → 标题栏显示完整路径」。</summary>
    private bool _titleShowsFullPath;

    /// <summary>
    /// 「视图 → 列设置」里被关掉的列。<strong>它不在 <see cref="_view"/> 里</strong>：
    /// 那一份是「设置 → 查看」那一页的数据形状，这一份归「视图」菜单管；
    /// 混在一起的话，设置页每次「应用」都会顺手把用户刚从菜单里关掉的列放回来
    /// —— 一个键只归一处管，这条规矩在 1.6.0 就是为这个立的。
    /// <para>它是<strong>全局</strong>的，不是"每个包一套列"：列的选择关于这个界面，
    /// 下一次打开任何包都该还是那些列。</para>
    /// </summary>
    private readonly List<string> _hiddenColumns;

    /// <summary>
    /// 工具栏现在是哪一档（大 / 小 / 隐藏）。<strong>它不在 <see cref="_view"/> 里</strong>：
    /// 那一份是「设置 → 查看」那一页的数据形状，而这一档归「视图 → 工具栏」那个二级菜单管 ——
    /// 一个键只归一处管（1.6.0 的规矩，4.1.0 的 <c>hidden_columns</c> 是同一条的应用）。
    /// <para>这一页原来那个「显示工具栏」开关因此整行删掉了：三档里已经有"隐藏"，
    /// 留两个入口管同一件事，就是留一对会互相盖掉的写方。</para>
    /// </summary>
    private ToolBarMode _toolBarMode;

    /// <summary>
    /// 「视图 → 图像预览」开不开（左栏底下那一格）。与 <see cref="_toolBarMode"/>、
    /// <see cref="_hiddenColumns"/> 同一个待遇：<strong>不在 <see cref="_view"/> 里</strong> ——
    /// 那一份是「设置 → 查看」那一页的数据形状，而这一项的主人是「视图」菜单。
    /// <para>缺省开着的理由写在 <see cref="SettingKeys.ViewPreview"/> 那条注释里；
    /// 与「置顶」相反，这一项<strong>落盘</strong>：版面是设置，姿势不是。</para>
    /// </summary>
    private bool _showPreview;

    public MainWindow(LangCatalog lang, AppSettings settings)
    {
        _lang = lang;
        _settings = settings;
        _view = ViewDefaults.Load(settings);
        _hiddenColumns = [.. settings.GetStringList(SettingKeys.HiddenColumns)];
        _toolBarMode = SettingKeys.ParseToolBarMode(settings.GetString(SettingKeys.ViewToolBar));
        _showPreview = settings.GetBool(SettingKeys.ViewPreview, fallback: true);

        Title = EngineTitle;
        Width = 1040;
        Height = 773;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 整块窗口就是一张圆角面板：系统标题栏被"扩展客户端区 + 无按钮"换掉，
        // 外面那圈透明边距留给圆角与阴影（规范 5.1）。窗口自己的底色必须是透明 ——
        // 不透明的话圆角外会剩四个方方的白角，比直角还难看。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

        // 底色、字体、图标这三样不在这里各写一遍：那是 WindowSurface 的活，
        // 而它顺手还接了"用户换图标时这扇窗跟着换"。
        this.WindowSurface();

        _homeContent = HomeView.Create(lang);

        _commands = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            [MainMenuModel.CommandExit] = Close,
            [MainMenuModel.CommandNewArchive] = OpenNewArchiveDialog,
            [MainMenuModel.CommandOpenArchive] = OpenArchiveAsync,
            [MainMenuModel.CommandCloseArchive] = CloseArchive,
            [MainMenuModel.CommandExtractArchive] = ExtractArchiveAsync,
            [MainMenuModel.CommandExtractTo] = ExtractToAsync,
            [MainMenuModel.CommandTestArchive] = TestArchiveAsync,
            [MainMenuModel.CommandNewWindow] = () => NewWindow(elevated: false),
            [MainMenuModel.CommandNewWindowAdmin] = () => NewWindow(elevated: true),
            [MainMenuModel.CommandRepairArchive] = () => RepairArchiveAsync(),
            [MainMenuModel.CommandSaveArchiveAs] = SaveArchiveAsAsync,
            [MainMenuModel.CommandDeleteArchive] = DeleteArchiveToRecycleBin,
            [MainMenuModel.CommandPermanentDeleteArchive] = PermanentlyDeleteArchive,
            [MainMenuModel.CommandOpenArchiveFolder] = OpenArchiveFolder,
            [MainMenuModel.CommandToggleTheme] = ToggleTheme,
            [MainMenuModel.CommandToggleOnTop] = ToggleTopMost,
            [MainMenuModel.CommandPreview] = TogglePreview,
            [MainMenuModel.CommandSettings] = OpenSettingsDialog,
            [MainMenuModel.CommandFindFiles] = OpenFindDialog,
            [MainMenuModel.CommandRenameEntry] = RenameEntryAsync,
            [MainMenuModel.CommandAddEntries] = AddEntriesAsync,
            [MainMenuModel.CommandDeleteEntries] = DeleteEntriesAsync,
            [MainMenuModel.CommandEditComment] = EditCommentAsync,
            [MainMenuModel.CommandCopyEntries] = CopyEntriesAsync,
            [MainMenuModel.CommandPasteEntries] = PasteEntriesAsync,
            [MainMenuModel.CommandCopyPaths] = CopyPathsAsync,
            [MainMenuModel.CommandRecoverPassword] = OpenPasswordRecoveryDialog,
            [MainMenuModel.CommandAbout] = OpenAboutDialog,
            [MainMenuModel.CommandOpenInstallFolder] = OpenInstallFolder,
            [MainMenuModel.CommandHomepage] = OpenHomepage,
        };

        var root = new DockPanel();

        _menuBar = MainMenuBar.Create(_lang, _commands, new RecentFilesMenu(
            LoadRecentFiles, OpenRecentFile, ClearRecentFiles),
            new ColumnMenu(() => _hiddenColumns, SetColumnVisible),
            new ToolBarMenu(() => _toolBarMode, SetToolBarMode),
            new CrcMenu(algorithm => ChecksumAsync(algorithm)));

        // 模型里 Enabled: true 的语义是"这条实装过了"。自称实装了却没接到命令，界面上就是
        // 一条黑字、能点、点下去什么都不发生 —— 那比灰着危险得多（文件菜单那四条置灰的同一条判断）。
        // 所以这里当场抛，而不是留给用户先点一下再发现。
        var unwired = MainMenuModel.Create().SelectMany(menu => menu.Items)
            .Where(item => item.Enabled && (item.CommandId is null
                || (!_commands.ContainsKey(item.CommandId) && !HandledElsewhere.Contains(item.CommandId))))
            .Select(item => $"'{item.Id}'（{item.TextKey}）")
            .ToArray();
        if (unwired.Length > 0)
        {
            throw new InvalidOperationException(
                $"这些菜单项在模型里是启用态，却没有接到命令：{string.Join("、", unwired)}");
        }

        // 九颗全部接上了：「查看」是 4.6.0 补上的最后一颗（图像预览），它以前是 2.6.0 留下的死占位。
        _toolBar = BuildToolBar();

        // 一行装完：图标 + 菜单 ………… 最小化 / 最大化 / 关闭。
        // 以前这是两行（标题文字单独一行，菜单第二行），用户的话是"上面的文字去掉，第二行整体上移"。
        // 版本号不删，但 3.1.0 起它不在这条行了，而在面板底部的视图栏右端（见 ViewBar）——
        // 那一行本来只有"图标 + 菜单"和"三个窗口按钮"两端，版本号挤在窗口按钮旁边
        // 既容易被读成第四颗按钮，也把右端的间距撑得忽大忽小。
        _titleBar = new TitleBar(this, _lang, resizable: true, leading: _menuBar);
        DockPanel.SetDock(_titleBar, Dock.Top);
        root.Children.Add(_titleBar);

        // 工具栏是一张<strong>浮在面板上的卡片</strong>（3.3.0，用户给的参考图），
        // 所以这一层不再是"卡片 + 一根发丝线"，只剩一圈留白 —— 卡片与面板的分界由影子读出来，
        // 再加一根线就是把同一件事说两遍（而且线是画上去的，投不出光）。
        // 留白同时是<strong>影子的地方</strong>：`Shadow.Lift` 弥散 10px，边距小于它就等于把影子切一刀。
        // 注意 Dock.Top 必须设在<strong>这层 Border</strong>上 —— 设在工具栏自己身上没用，
        // DockPanel 只看自己直接子元素的值（1.4.0 就踩过：那样菜单栏会变成左边一整列）。
        _headerHost = new Border
        {
            Child = _toolBar,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(Design.Space.X3, Design.Space.X2, Design.Space.X3, Design.Space.X2),
        };
        DockPanel.SetDock(_headerHost, Dock.Top);
        root.Children.Add(_headerHost);

        root.Children.Add(_contentHost);

        // 整块面板：外面那一圈透明边距留给圆角与阴影（阴影伸不出去就会被窗口切一刀），
        // 里面是同一张表面。<strong>没有描边</strong> —— 参考图里也没有：一圈均匀的灰线是画上去的，
        // 什么光都投不出这种东西，它正是"不自然"的来源。边界由面板与背景的明暗差 + 一层软影读出来。
        _surface = new Border
        {
            Margin = new Thickness(Design.Float.Margin),
            CornerRadius = Design.Radius.Panel,
            BorderThickness = new Thickness(0),
            BoxShadow = Design.Shadow.Floating,
            Child = root,
        };
        Content = _surface;

        ShowHome();
        ApplyViewDefaults();
        RegisterShortcuts(_commands);
        AcceptFileDrops();

        // 最大化时圆角与边距要收掉：留着一圈透明边，最大化后的窗口会比工作区小一圈，
        // 四个角还露出桌面 —— 那是"没铺满"，不是"圆角好看"。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                FitSurfaceToState();
            }
        };

        // 主题放在最后：它要重画上面已经建好的那一套外壳，也订阅了 Design 的变化事件。
        Design.ActiveChanged += ApplyTheme;
        Opened += (_, _) => ApplyTheme();
        Closed += (_, _) => Design.ActiveChanged -= ApplyTheme;
    }

    /// <summary>
    /// 把当前调色板刷到已经建好的外壳上，并重做内容区。
    /// <para>为什么要"重做"而不是"改属性"：本工程没有 XAML，控件的颜色是在构造时从
    /// <see cref="Design"/> 取一次就固化到实例上的（菜单项、工具栏按钮、列表表头都是这样）。
    /// 逐个改回去等于再写一遍构造函数，而且一定会漏掉几个 —— 所以外壳这种"只有几处底色"的
    /// 直接改，内容那种"整棵子树都是构造期决定"的重新建一份。</para>
    /// </summary>
    private void ApplyTheme()
    {
        var dark = Design.Active.Name == "dark";

        // 窗口本身永远是透明的（圆角外要能看见桌面），有颜色的是那层面板。
        Background = Brushes.Transparent;
        _surface.Background = Design.Surface.Base;
        _titleBar.Repaint();
        _menuBar.Background = Brushes.Transparent;
        _menuBar.Foreground = Design.Ink.Primary;

        // 工具栏整条重做：它的图标与文字颜色是构造时取的一次，光刷卡片底色救不回来。
        _toolBar = BuildToolBar();
        _headerHost.Child = _toolBar;

        _homeContent = HomeView.Create(_lang);

        // 正看着包就重建那个包的视图，否则重建首页。位置（首页 / 内容）要保住，
        // 不然用户改个主题就被扔回首页。
        if (_listing is { } listing)
        {
            ShowArchiveView(listing);
        }
        else
        {
            ShowHome();
        }

        WindowTheme.Apply(this, dark);
    }

    /// <summary>
    /// 最大化时收掉圆角与透明边距。留着的话最大化后四周会有一圈透明缝、四个角露出桌面 ——
    /// 那是"没铺满"，不是"圆角好看"。还原时再放回去。
    /// </summary>
    private void FitSurfaceToState()
    {
        var maximized = WindowState == WindowState.Maximized;
        _surface.Margin = maximized ? new Thickness(Design.Space.X1) : new Thickness(Design.Float.Margin);
        _surface.CornerRadius = maximized ? Design.Radius.Xs : Design.Radius.Panel;
    }

    /// <summary>
    /// 按当前调色板与当前那一档做一条新工具栏。「视图 → 工具栏」选的是哪一档在这里一并带上，
    /// 否则换完主题工具栏会悄悄跑回大档（<see cref="ApplyViewDefaults"/> 那次已经过去了）。
    /// <para>每一颗都从 <see cref="_commands"/> 里取，不在这里另写一遍"它做什么"。
    /// 「查看」与「切换语言」两颗<strong>不带命令 id</strong>：前者是预览面板，功能本身还没做，
    /// 没有命令可给，于是它建出来就是灰的而且谁也不会去点亮它；后者的动作是"跳到设置那一页"，
    /// 菜单里没有同一条，不该被按命令的那一趟管。</para>
    /// </summary>
    private Control BuildToolBar()
    {
        var bar = MainToolBar.Create(_lang, new MainToolBarActions(
            OpenArchive: _commands[MainMenuModel.CommandOpenArchive],
            ExtractArchive: _commands[MainMenuModel.CommandExtractArchive],
            NewArchive: _commands[MainMenuModel.CommandNewArchive],
            AddFiles: _commands[MainMenuModel.CommandAddEntries],
            DeleteFiles: _commands[MainMenuModel.CommandDeleteEntries],
            TestArchive: _commands[MainMenuModel.CommandTestArchive],
            PickChecksum: algorithm => ChecksumAsync(algorithm),
            TogglePreview: _commands[MainMenuModel.CommandPreview],
            SwitchLanguage: () => OpenSettingsDialogOn("TEXT_SETTINGS_LANGUAGE")),
            _toolBarMode);

        // 「隐藏」要跟着带上，否则换完主题那条被藏起来的工具栏会悄悄跑回来。
        bar.IsVisible = _toolBarMode != ToolBarMode.Hidden;
        return bar;
    }

    // ---------- 拖拽 ----------

    /// <summary>
    /// 让主窗口接受从资源管理器拖进来的文件与文件夹。
    /// <para>注册在窗口本身而不是某个子控件上：拖到菜单、工具栏、列表还是空白处都应当算，
    /// 而 Avalonia 的拖放事件会从命中的子控件冒泡到这里。</para>
    /// </summary>
    private void AcceptFileDrops()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        // 不给 Copy 效果的话光标会变成"禁止"，用户当场就知道这一拖没有意义。
        // 这里只问"有没有文件"，不去取路径：DragOver 在鼠标移动中反复触发，
        // 每次都把 shell 的数据对象展开一遍不值当。
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// 放下这一批路径。做什么由 <see cref="ShellTasks.RouteDroppedPaths"/> 决定 ——
    /// 那个判断与右键共用语汇，且在 Core 里测过；这里只负责把结果接到窗口上。
    /// </summary>
    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e.DataTransfer);

        if (paths.Count == 0)
        {
            return;
        }

        switch (ShellTasks.RouteDroppedPaths(paths))
        {
            case ShellTask.Open:
                await OpenArchive(paths[0]);
                break;
            case ShellTask.CompressDialog:
                OpenNewArchiveDialog(paths);
                break;
        }
    }

    /// <summary>
    /// 拖放数据里的文件路径。不是文件的数据（纯文本、控件内部拖拽）一律当作没拖。
    /// <para><see cref="IStorageItem.Path"/> 是 <see cref="Uri"/> 而不是字符串，
    /// 本地文件要拿 <c>LocalPath</c> —— 直接 ToString 会得到 <c>file:///C:/…</c>，
    /// 那个路径没有哪个 Win32 API 认。</para>
    /// </summary>
    private static IReadOnlyList<string> DroppedPaths(IDataTransfer data)
        => data.TryGetFiles() is not { Length: > 0 } files
            ? []
            : [.. files.Select(item => item.Path?.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)];

    /// <summary>
    /// 把「设置 → 查看」应用到当前窗口。工具栏是这里唯一能立刻生效的一块 ——
    /// 树、状态栏、视图样式属于压缩包视图内部，那些是在下次打开包（或本就在看包时重开视图）时生效。
    /// </summary>
    private void ApplyViewDefaults()
    {
        _view = ViewDefaults.Load(_settings);

        // 主题也在这一组里：换了偏好就翻调色板，Design 的事件会带回 ApplyTheme 重画外壳与内容。
        Design.Apply(_view.Theme);
        _toolBar.IsVisible = _toolBarMode != ToolBarMode.Hidden;
        _titleShowsFullPath = _settings.GetBool(SettingKeys.TitleBarFullPath, fallback: false);

        // 标题是"当前状态"的一部分，改了设置得立刻反映出来，而不是等下次打开包。
        if (_listing is not null)
        {
            Title = ArchiveTitle(_listing.ArchivePath);
        }
    }

    // ---------- 视图切换 ----------

    private void ShowHome()
    {
        _browser?.Dispose();
        _browser = null;
        _listing = null;
        CloseFindWindow();
        _contentHost.Content = _homeContent;
        Title = EngineTitle;
        RefreshCommandStates();
    }

    /// <summary>
    /// 重算所有"要看当前视图与选择才定得下来"的命令的灰与亮（以及勾）。
    /// <para>四个调用点：换视图（首页 ↔ 压缩包）、选择变了、<strong>换掉一条工具栏</strong>、
    /// <strong>置顶变了</strong>。第三个是 4.4.0 引入的洞：换档会重做整条工具栏，而新那一条上每一颗的 <c>IsEnabled</c>
    /// 都是构造时那个"命令接上了就是亮"的初值 —— 不重算，在首页上刚切完档就会得到两颗
    /// 亮着的「添加」「删除」，点下去 <c>_listing is null</c> 静默返回。
    /// "能点、点了什么也不发生"正是这条规矩从 0.9.x 就在挡的形状。</para>
    /// </summary>
    private void RefreshCommandStates()
    {
        SetArchiveOnlyCommandsEnabled(_listing is not null);
        UpdateEditCommandStates();
        SetCommandChecked(MainMenuModel.CommandToggleOnTop, Topmost);
        SetCommandChecked(MainMenuModel.CommandPreview, _showPreview);
    }

    private void ShowArchiveView(ArchiveListing listing)
    {
        _browser?.Dispose();
        _browser = new ArchiveView(_lang, listing, _view, _hiddenColumns, _showPreview);
        _listing = listing;
        CloseFindWindow();
        _contentHost.Content = _browser.Root;
        Title = ArchiveTitle(listing.ArchivePath);

        // 「编辑」那几项里，有三项要"选中了东西"才亮，而选择是视图内部的状态。
        // 订阅它而不是每次改完自己再算一遍：选择会变的地方有四处（点行、点树、导航、定位），
        // 逐个去补必然漏掉一两处。
        _browser.SelectionChanged += UpdateEditCommandStates;
        RefreshCommandStates();
    }

    // ---------- 查找 → 文件查找 ----------

    /// <summary>
    /// 换包或关包时把查找窗口一起收掉。
    /// <para>它手里拿的是<strong>打开时那一份清单</strong>，包一换那些行就属于另一个文件了 ——
    /// 留着它，用户点「定位」会跳到一个已经不存在的行。让它跟着清单一起结束，
    /// 比在里面做"这份结果已经过期"的提示干净。</para>
    /// </summary>
    private void CloseFindWindow()
    {
        if (_find is { } window)
        {
            _find = null;
            window.Close();
        }
    }

    /// <summary>
    /// 打开（或唤回）查找窗口。<strong>非模态</strong>，而且同一时刻只留一扇 ——
    /// 按两次 Ctrl+F 开出两扇一模一样的窗口，用户会开始数哪扇是新的。
    /// </summary>
    private void OpenFindDialog()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        if (_find is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new FindWindow(_lang, listing);
        window.RevealRequested += entry => _browser?.RevealEntry(entry);
        window.Closed += (_, _) => _find = null;
        _find = window;
        window.Show(this);
    }

    // ---------- 帮助 → 关于 / 安装目录 / 官网 ----------

    /// <summary>
    /// 「工具 → 密码恢复」。目标默认是<strong>当前正开着的这个包</strong>，没开着就留空让人自己选 ——
    /// 忘了口令的包往往根本打不开，所以这里不能强依赖"已经打开了"。
    /// </summary>
    private void OpenPasswordRecoveryDialog()
    {
        if (_recovery is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new PasswordRecoveryWindow(
            _lang,
            _listing?.ArchivePath,
            PickArchiveAsync,
            PickDictionaryAsync,
            (archive, password) => Session.Passwords.Remember(archive, password));
        window.Closed += (_, _) => _recovery = null;
        _recovery = window;
        window.Show(this);
    }

    private async Task<string?> PickDictionaryAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _lang.Label("TEXT_RECOVERY_DICT_TITLE"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_lang.Get("TEXT_RECOVERY_DICT_KIND")) { Patterns = ["*.txt", "*.lst", "*.dic"] },
                new FilePickerFileType(_lang.Get("TEXT_ALL_FILES")) { Patterns = ["*.*"] },
            ],
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>「帮助 → 关于 Andzip」（F1）。同一时刻只留一扇，再按一次只是把它唤到前面。</summary>
    private void OpenAboutDialog()
    {
        if (_about is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new AboutWindow(_lang);
        window.Closed += (_, _) => _about = null;
        _about = window;
        window.Show(this);
    }

    /// <summary>
    /// 「帮助 → 打开 Andzip 安装目录」。
    /// <para>开的是<strong>这个正在运行的 exe 自己所在的目录</strong>：绿色版没有"安装"这一步，
    /// 而一台机器上装了"给自己"与"给所有用户"两份时，只有这一份答案不会指错地方 ——
    /// 用户对着屏幕上这个窗口点它，要看到的就是这个窗口的老家。</para>
    /// </summary>
    private void OpenInstallFolder()
    {
        if (AboutWindow.AppDirectory() is not { } directory)
        {
            return;
        }

        OpenInExplorer(directory);
    }

    /// <summary>「帮助 → 转到官网」：交给系统默认浏览器。地址只有一个来源，见 <c>AndzipLinks</c>。</summary>
    private void OpenHomepage() => AboutWindow.OpenUrl(AndzipLinks.Homepage);

    // ---------- 文件 → CRC（算校验和）/ 新建程序窗口（普通与管理员） ----------

    /// <summary>
    /// 「文件 → CRC」那一排与工具栏那颗 CRC：给屏幕上正看着的那几样算一次摘要。三种情况，
    /// 与用户此刻指着的东西对齐：
    /// ① 开着包且有选中 → 算<strong>那些成员解压出来的字节</strong>；
    /// ② 开着包但没选中（或只点了文件夹）→ 算<strong>这个包文件本身</strong> ——
    ///   "我下的这个包对不对"是这个问题最常见的样子；
    /// ③ 首页 → 先挑文件（可多选），算磁盘上的字节。
    /// <para>①算的是解压之后的数据，所以它与详情栏那一列 CRC 不是一回事：那一列抄的是包里
    /// 记着的数，这一排是我们自己算一遍。两个数不一样就说明记录与数据不符 —— 那是
    /// 「测试压缩包」要回答的问题，这里只报数字，不替用户当裁判。</para>
    /// <para>进度条走真字节数（成员大小清单里就有）。取消落在<strong>项与项之间</strong>：
    /// 内核的解压没有可中断的点，正在解的那一格会算完再停，与 <c>ArchiveTester</c> 同一句实话。</para>
    /// </summary>
    private async void ChecksumAsync(ChecksumAlgorithm algorithm)
    {
        var archivePath = _listing?.ArchivePath;
        var entries = archivePath is null
            ? []
            : SelectedEntries().Where(entry => !entry.IsDirectory).ToList();
        IReadOnlyList<string> files = [];

        if (archivePath is null)
        {
            files = await PickFilesForChecksumAsync();
            if (files.Count == 0)
            {
                return;   // 挑选框里按了取消：那不是失败，别说话
            }
        }
        else if (entries.Count == 0)
        {
            files = [archivePath];
        }

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_CHECKSUM",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        _ = progress.ShowDialog(this);

        IReadOnlyList<HashRow> rows;
        try
        {
            var sink = progress.CreateSink();
            rows = await Task.Run(
                () => ComputeChecksum(archivePath, entries, files, algorithm, sink, cts.Token),
                cts.Token);
        }
        catch (Exception ex)
        {
            progress.Close();
            // 取消不算故障；其余（文件被占用、路径没了）原样转达。
            if (ex is not OperationCanceledException)
            {
                await ShowMessageAsync("TEXT_TITLE_ERROR", ex.Message);
            }

            return;
        }

        progress.Close();

        if (cts.IsCancellationRequested)
        {
            return;
        }

        await ShowMessageAsync("TEXT_TITLE_CHECKSUM", ChecksumText(algorithm, rows));
    }

    /// <summary>
    /// 后台那一半。成员要现开一个读取会话：它的寿命得罩住整次计算，所以不能借视图里那一个
    /// （视图可能中途被换掉，而会话释放时正有一次读在跑 —— 4.6.3 刚为这件事补过一刀）。
    /// </summary>
    private static IReadOnlyList<HashRow> ComputeChecksum(
        string? archivePath,
        IReadOnlyList<ArchiveEntryInfo> entries,
        IReadOnlyList<string> files,
        ChecksumAlgorithm algorithm,
        IProgress<ArchiveProgress> report,
        CancellationToken cancel)
    {
        void Emit(HashProgress progress) => report.Report(new ArchiveProgress(
            ArchiveOperation.Hash, progress.DoneBytes, progress.TotalBytes, progress.Current,
            progress.ItemsDone, progress.ItemCount));

        if (entries.Count > 0 && archivePath is not null)
        {
            using var session = ArchiveContentSession.Open(archivePath);
            return ChecksumCalculator.Compute(
                [algorithm],
                [.. entries.Select(entry => new HashItem(
                    entry.Path,
                    entry.Size,
                    stream => session.ReadEntry(entry.Index, stream)))],
                Emit,
                cancel);
        }

        return ChecksumCalculator.Compute(
            [algorithm],
            [.. files.Select(path => new HashItem(
                Path.GetFileName(path),
                File.Exists(path) ? new FileInfo(path).Length : 0L,
                stream =>
                {
                    using var source = File.OpenRead(path);
                    source.CopyTo(stream, 64 * 1024);
                }))],
            Emit,
            cancel);
    }

    /// <summary>
    /// 结果那一屏的正文：每一项一段，名字一行、下面每种算法一行。
    /// <para>读不到的一项照样列出来并写明为什么 —— 只报成功的那几项，等于把"有两项没算"
    /// 这件事藏起来（与「测试压缩包」报全部坏成员同一条判断）。</para>
    /// </summary>
    private string ChecksumText(ChecksumAlgorithm chosen, IReadOnlyList<HashRow> rows)
    {
        var names = chosen.IsAll
            ? string.Join(" / ", ChecksumAlgorithm.Ten.Select(algorithm => algorithm.Label))
            : chosen.Label;

        var lines = new List<string> { names };

        foreach (var row in rows)
        {
            lines.Add(row.Name);

            if (row.Failed)
            {
                lines.Add($"    {row.Error}");
                continue;
            }

            var digests = row.Digests;
            for (var i = 0; i < digests.Count; i++)
            {
                lines.Add($"    {LabelOf(chosen, i)}    {digests[i]}");
            }
        }

        var failed = rows.Count(row => row.Failed);
        if (failed > 0)
        {
            lines.Add(_lang.Format("TEXT_CHECKSUM_PART_FAILED",
                ("done", (rows.Count - failed).ToString()), ("total", rows.Count.ToString())));
        }

        return string.Join(Environment.NewLine, lines);

        static string LabelOf(ChecksumAlgorithm chosen, int index) => chosen.IsAll
            ? ChecksumAlgorithm.Ten[index].Label
            : chosen.Label;
    }

    /// <summary>首页那一档的挑选框：任意文件、可多选。与「打开压缩包」不同，这里不该按
    /// 压缩包过滤 —— 校验和最常见的对象就是手上一个不相干的下载件。</summary>
    private async Task<IReadOnlyList<string>> PickFilesForChecksumAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _lang.Get("TEXT_TITLE_CHECKSUM"),
            AllowMultiple = true,
        });

        return [.. picked
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))];
    }

    /// <summary>
    /// 「新建程序窗口」与它的管理员版（Ctrl+Shift+N / Ctrl+Alt+N）：<strong>另起一个进程</strong>，
    /// 不是在当前窗口里再开一块内容区 —— 参考软件那两条也是两个进程，管理员那一扇才装得下
    /// 后面要做的"改系统位置"这类事。
    /// <para>命令行必须自己带上 <c>/task=open</c>：不带开关的那一条是<strong>双击</strong>的语义，
    /// 做什么由「设置 → 解压 → 双击压缩包文件时」决定 —— 那一档现在可以就是「解压」，
    /// 少了这个开关，"开一扇新窗口"就变成"把包解压了"。</para>
    /// <para>提权那一条走 <c>Verb = "runas"</c>：UAC 框由系统弹、由用户按，这一条命令本身就是
    /// 用户点出来的，所以不算静默提权。用户按取消（<c>ERROR_CANCELLED</c>）与这台机器不让他提权
    /// 是两件事，但界面上都只该说一句"没起来" —— 不重试，更不悄悄换成不提权的那一条。</para>
    /// </summary>
    private void NewWindow(bool elevated)
    {
        if (string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return;
        }

        var arguments = _listing is { } listing
            ? $"\"{listing.ArchivePath}\" {ShellTasks.Switch(ShellTask.Open)}"
            : ShellTasks.Switch(ShellTask.Open);

        var start = new ProcessStartInfo(Environment.ProcessPath)
        {
            Arguments = arguments,
            // 只有 ShellExecute 那条路认 Verb；不提权时用 CreateProcess，别绕系统那一层。
            UseShellExecute = elevated,
        };

        if (elevated)
        {
            start.Verb = "runas";
        }

        try
        {
            Process.Start(start);
        }
        catch (Exception ex)
        {
            // 1223 = 用户在 UAC 框上按了取消。这一种要说人话，其余把系统给的原因原样转达。
            _ = ShowMessageAsync("TEXT_TITLE_NEW_WINDOW", ex is Win32Exception { NativeErrorCode: 1223 }
                ? _lang.Get("TEXT_NEW_WINDOW_CANCELED")
                : _lang.Format("TEXT_NEW_WINDOW_FAILED", ("reason", ex.Message)));
        }
    }

    // ---------- 文件 → 另存为 / 删除 / 永久删除 / 打开位置 ----------

    /// <summary>
    /// 按命令把"只有开着压缩包才能用"的那几项点亮或置灰。
    /// <para>用 <c>Tag</c>（<see cref="MainMenuBar"/> 与 <see cref="MainToolBar"/> 建的时候写进
    /// CommandId）而不是另存一份控件引用：菜单结构或工具栏排布改了不用回来改这张表，
    /// 也不会漏掉新加的一项。</para>
    /// </summary>
    private void SetArchiveOnlyCommandsEnabled(bool enabled)
    {
        foreach (var carrier in CommandCarriers())
        {
            if (carrier.Tag is string command && MainMenuModel.ArchiveOnlyCommands.Contains(command))
            {
                carrier.IsEnabled = enabled;
            }
        }
    }

    private static IEnumerable<MenuItem> WalkItems(ItemsControl parent)
    {
        foreach (var child in parent.Items.OfType<MenuItem>())
        {
            yield return child;

            foreach (var nested in WalkItems(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 身上带着命令 id 的那些控件：菜单项 + 工具栏那几颗。
    /// <para>灰与亮<strong>只走这一个口子</strong>。同一个命令在菜单里灰着、在工具栏上却能点
    /// （或反过来），用户读到的是"其中一处坏了"，而真实原因是两条判据写在了两个地方 ——
    /// 4.2.0 把工具栏那三颗接上时，这个形状第一次真的会出现，所以先合并再接线。</para>
    /// <para>走逻辑树而不是视觉树：工具栏在构造时还没挂进窗口，按视觉子节点找会一个都找不到。</para>
    /// </summary>
    private IEnumerable<Control> CommandCarriers()
    {
        foreach (var item in WalkItems(_menuBar))
        {
            yield return item;
        }

        foreach (var cell in _toolBar.GetLogicalDescendants().OfType<Control>())
        {
            yield return cell;
        }
    }

    // ---------- 编辑（重命名 / 添加 / 删除 / 注释 / 复制 / 粘贴 / 复制完整路径） ----------

    private IReadOnlyList<ArchiveEntryInfo> SelectedEntries()
        => [.. (_browser?.SelectedRows ?? []).Where(entry => entry.Name != "..")];

    private static string? CurrentPassword(string archivePath)
        => Session.Passwords.TryGet(archivePath, out var cached) ? cached : null;

    /// <summary>
    /// 按"当前开着什么包、选中了什么"重算「编辑」那七项的灰与亮 —— 菜单里那七行与工具栏上
    /// 的「添加」「删除」两颗一起算（它们带的是同一个命令 id，见 <see cref="CommandCarriers"/>）。
    /// <para>条件写在这里而不是菜单模型里：<strong>只有窗口看得见当前选择</strong>。
    /// 而"这个格式改不改得动"与"这个格式有没有注释"的判据不在这里重写一遍 ——
    /// 那是 <see cref="ArchiveUpdater.CanUpdate"/> 与 <see cref="ZipComment.CanEdit"/> 的事，
    /// 抄一份写死在界面里，早晚会与真动手那一层说的不是同一件事。</para>
    /// </summary>
    private void UpdateEditCommandStates()
    {
        var entries = SelectedEntries();
        var hasSelection = entries.Count > 0;
        var singleFile = entries.Count == 1 && !entries[0].IsDirectory;
        var editable = ArchiveUpdater.CanUpdate(_listing?.FormatName);

        SetCommandEnabled(MainMenuModel.CommandRenameEntry, editable && singleFile);
        SetCommandEnabled(MainMenuModel.CommandAddEntries, editable);
        SetCommandEnabled(MainMenuModel.CommandDeleteEntries, editable && hasSelection);
        SetCommandEnabled(MainMenuModel.CommandEditComment, ZipComment.CanEdit(_listing?.FormatName));
        SetCommandEnabled(MainMenuModel.CommandCopyEntries, hasSelection);
        SetCommandEnabled(MainMenuModel.CommandPasteEntries, editable);
        SetCommandEnabled(MainMenuModel.CommandCopyPaths, hasSelection);
    }

    private void SetCommandEnabled(string commandId, bool enabled)
    {
        foreach (var carrier in CommandCarriers())
        {
            if (carrier.Tag is string command && command == commandId)
            {
                carrier.IsEnabled = enabled;
            }
        }
    }

    /// <summary>
    /// 给带着这个命令 id 的菜单项挂上或摘掉那一枚勾。
    /// <para>只走菜单，不跟 <see cref="CommandCarriers"/> 一起走工具栏：工具栏那一格画的是它自己的
    /// 图标，勾没有地方放，而「置顶」本来也没有第二个入口（与「列设置」「工具栏」一样只有一排）。</para>
    /// <para>已经画对了就不重画 —— 勾就是 <c>Icon</c> 那一格本身，所以"现在有没有勾"直接看它。
    /// 这一趟挂在 <see cref="RefreshCommandStates"/> 里，而那个方法在每次选择变化时都会跑，
    /// 给一个正展开着的菜单行换一个新控件是能被看见的闪一下。</para>
    /// <para>为什么要挂上去而不是"菜单只建一次，所以点的时候顺手改一下就够了"：
    /// 4.4.0 那次就是"状态改了、画面是照旧值建的那批控件"（见开发记录第七节第 88 条）。</para>
    /// </summary>
    private void SetCommandChecked(string commandId, bool isChecked)
    {
        foreach (var item in WalkItems(_menuBar))
        {
            if (item.Tag is string command && command == commandId && (item.Icon is not null) != isChecked)
            {
                item.Icon = isChecked ? MainMenuBar.Check() : null;
            }
        }
    }

    /// <summary>
    /// 选中项 → 要被"动"的成员下标。目录会展开成它底下的每一个成员 ——
    /// 引擎那边只有"留哪些"这一个动作，半个树留在包里比拒绝更糟。
    /// </summary>
    private static List<int> ExpandToIndices(
        ArchiveListing listing,
        IEnumerable<ArchiveEntryInfo> selected)
    {
        var indices = new HashSet<int>();
        foreach (var entry in selected)
        {
            if (!entry.IsDirectory)
            {
                indices.Add(entry.Index);
                continue;
            }

            foreach (var member in listing.Entries.Where(
                member => member.Path.StartsWith(entry.Path, StringComparison.Ordinal)))
            {
                indices.Add(member.Index);
            }
        }

        return [.. indices];
    }

    /// <summary>重命名（F2）：先弹一个文本框预填原名字，再把新名字交给 Core 去搬。
    /// <para>改的只是<strong>叶子名</strong>，目录前缀原样带过去 —— 让用户在这里输 <c>a/b.txt</c>
    /// 等于把"改个名字"变成"搬个位置"，那是另一件事。</para></summary>
    private async void RenameEntryAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var entries = SelectedEntries();
        if (entries.Count != 1 || entries[0].IsDirectory)
        {
            return;
        }

        var entry = entries[0];
        var prompt = new TextPromptWindow(
            _lang,
            "MENUSTR_RENAME_FILE",
            "TEXT_LABEL_RENAME_ENTRY",
            initial: entry.Name,
            validate: text => ValidateLeafName(listing, entry, text));

        if (await prompt.ShowDialog<bool>(this) != true)
        {
            return;
        }

        var newName = prompt.Text.Trim();
        if (string.Equals(newName, entry.Name, StringComparison.Ordinal))
        {
            return;   // 名字没动就别白搬一次（改名要把这个成员整个解出来再压回去）
        }

        var parent = ArchiveHierarchy.ParentPathOf(entry.Path);
        var newPath = parent.Length == 0 ? newName : parent + "/" + newName;

        var folder = CurrentFolderPath();
        if (await RunEditAsync("MENUSTR_RENAME_FILE", "TEXT_STATUS_UPDATING_AND_REPACKING",
                (progress, token) => ArchiveUpdater.Rename(
                    listing.ArchivePath, entry.Index, newPath, CurrentPassword(listing.ArchivePath),
                    progress, token)))
        {
            ReloadAfterEdit(listing.ArchivePath, folder);
        }
    }

    /// <summary>新名字是不是能用的叶子名。返回一句要显示的话，或 null 表示没问题。</summary>
    private string? ValidateLeafName(ArchiveListing listing, ArchiveEntryInfo entry, string text)
    {
        var name = text.Trim();
        if (name.Length == 0)
        {
            return _lang.Label("TEXT_ERR_RENAME_EMPTY");
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0 || name is "." or "..")
        {
            return _lang.Label("TEXT_ERR_RENAME_INVALID");
        }

        var parent = ArchiveHierarchy.ParentPathOf(entry.Path);
        var candidate = parent.Length == 0 ? name : parent + "/" + name;
        var clash = listing.Entries.Any(member =>
            member.Index != entry.Index
            && string.Equals(member.Path.TrimEnd('/'), candidate, StringComparison.OrdinalIgnoreCase));

        return clash ? _lang.Label("TEXT_ERR_RENAME_DUPLICATE") : null;
    }

    /// <summary>添加文件（Ins）：往<strong>当前正看的那一层</strong>里加。落在根上会让
    /// "我在 docs 里，加进来的东西却跑到了包根"变成第二个谜题。</summary>
    private async void AddEntriesAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _lang.Label("MENUSTR_ADD_FILE"),
            AllowMultiple = true,
        });

        var paths = picked
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToArray();

        if (paths.Length == 0)
        {
            return;
        }

        await AddPathsToArchiveAsync(listing, paths);
    }

    /// <summary>
    /// 把磁盘上的这批路径加进包里（「添加文件」与「从剪贴板粘贴」共用一条）。
    /// <para>同名的那几项<strong>先问一句再替换</strong>：引擎的"留哪些"语义里，覆盖就是
    /// "把同名的从保留名单里划掉"，划错了没有任何反悔的余地 —— 而"我粘个文件把包里原来的目录整个顶掉"
    /// 是用户完全预料不到的后果。</para>
    /// </summary>
    private async Task AddPathsToArchiveAsync(ArchiveListing listing, IReadOnlyList<string> paths)
    {
        var folder = CurrentFolderPath();
        var incoming = new List<ArchiveAddedMember>();
        var targets = new List<string>();

        foreach (var path in paths)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (name.Length == 0)
            {
                continue;
            }

            var entryPath = folder.Length == 0 ? name : folder + "/" + name;
            incoming.Add(new ArchiveAddedMember(entryPath, SourcePath: trimmed));
            targets.Add(entryPath);
        }

        if (incoming.Count == 0)
        {
            return;
        }

        var shadowed = OccupiedIndices(listing, targets);
        var replace = shadowed.Count > 0;
        if (replace && !await MessageWindow.ConfirmAsync(
                this,
                _lang,
                _lang.Get("MENUSTR_ADD_FILE"),
                _lang.Format("TEXT_CONFIRM_REPLACE_ENTRIES", ("count", shadowed.Count.ToString())),
                "TEXT_BTN_OK"))
        {
            return;
        }

        if (await RunEditAsync("MENUSTR_ADD_FILE", "TEXT_STATUS_UPDATING",
                (progress, token) => ArchiveUpdater.Add(
                    listing.ArchivePath, incoming, replace, CurrentPassword(listing.ArchivePath),
                    progress, token)))
        {
            ReloadAfterEdit(listing.ArchivePath, folder);
        }
    }

    /// <summary>包里"占住"这些路径的成员下标：路径相同的那一个，以及同名目录底下的全部后代。</summary>
    private static List<int> OccupiedIndices(ArchiveListing listing, IReadOnlyList<string> entryPaths)
    {
        var bare = new HashSet<string>(
            entryPaths.Select(path => path.TrimEnd('/')),
            StringComparer.Ordinal);
        var prefixes = bare.Select(path => path + "/").ToArray();

        return
        [
            .. listing.Entries
                .Where(member => bare.Contains(member.Path.TrimEnd('/'))
                    || prefixes.Any(prefix => member.Path.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(member => member.Index),
        ];
    }

    /// <summary>删除文件（Del）：确认一次，然后连选中的目录整棵一起删。</summary>
    private async void DeleteEntriesAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var entries = SelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }

        var doomed = ExpandToIndices(listing, entries);
        var confirmed = await MessageWindow.ConfirmAsync(
            this,
            _lang,
            _lang.Get("MENUSTR_DELETE_FILE"),
            _lang.Format("TEXT_CONFIRM_DELETE_ENTRIES", ("count", doomed.Count.ToString())),
            "TEXT_BTN_DELETE");

        if (!confirmed)
        {
            return;
        }

        var folder = CurrentFolderPath();
        if (await RunEditAsync("MENUSTR_DELETE_FILE", "TEXT_STATUS_UPDATING",
                (progress, token) => ArchiveUpdater.Remove(
                    listing.ArchivePath, doomed, CurrentPassword(listing.ArchivePath), progress, token)))
        {
            ReloadAfterEdit(listing.ArchivePath, folder);
        }
    }

    /// <summary>
    /// 压缩文件注释：只有 ZIP 的结束记录里带得下这一句。
    /// <para><strong>原文没改就不写回去</strong>：读的时候我们只能猜编码（先 UTF-8 再 Latin-1），
    /// 而写的时候一律按 UTF-8 —— 打开看一眼再点确定，会把别的工具用系统代码页写的注释改写成我们的编码。
    /// 这一条判据写在 <see cref="ZipComment"/> 上，这里只是照着做。</para>
    /// </summary>
    private async void EditCommentAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        if (!ZipComment.CanEdit(listing.FormatName))
        {
            await ShowMessageAsync("MENUSTR_EDIT_COMMENT", _lang.Get("TEXT_ERR_COMMENT_NOT_SUPPORTED"));
            return;
        }

        string? current;
        try
        {
            current = await Task.Run(() => ZipComment.Read(listing.ArchivePath));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("MENUSTR_EDIT_COMMENT", _lang.Get("TEXT_ERR_COMMENT_FAILED") + ex.Message);
            return;
        }

        var prompt = new TextPromptWindow(
            _lang,
            "MENUSTR_EDIT_COMMENT",
            "TEXT_LABEL_ARCHIVE_COMMENT",
            initial: current,
            hintKey: "TEXT_COMMENT_HINT",
            multiline: true,
            allowEmpty: true,
            maxBytes: ZipComment.MaxBytes);

        if (await prompt.ShowDialog<bool>(this) != true)
        {
            return;
        }

        var text = prompt.Text;
        if (string.Equals(text, current, StringComparison.Ordinal))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_WRITING_COMMENT",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));
        _ = progress.ShowDialog(this);

        try
        {
            await Task.Run(() => ZipComment.Write(listing.ArchivePath, text), cts.Token);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return;
        }
        catch (ArchiveConstraintException ex)
        {
            progress.Close();
            await ShowMessageAsync("MENUSTR_EDIT_COMMENT", ReasonText(ex.Reason, "TEXT_ERR_COMMENT_FAILED"));
            return;
        }
        catch (Exception ex)
        {
            progress.Close();
            await ShowMessageAsync("MENUSTR_EDIT_COMMENT", _lang.Get("TEXT_ERR_COMMENT_FAILED") + ex.Message);
            return;
        }

        progress.Close();

        // 注释不进成员清单，「编辑」那几项的点亮状态没有任何变化，所以不重开视图 ——
        // 重开一次会把用户刚看的那一层收掉，代价比"顺手续一次"大。
    }

    /// <summary>
    /// 将文件复制到剪贴板（Ctrl+C）：先把选中的东西解到临时目录，再把这几个路径放进剪贴板。
    /// <para>剪贴板里放的是<strong>我选的那几样</strong>（整个文件夹就放文件夹那一个路径），
    /// 而不是展开后的每一个文件 —— 后者粘出来是一地散件，包内结构全丢了。</para>
    /// </summary>
    private async void CopyEntriesAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var entries = SelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }

        var staging = ClipboardStagingFolder();
        var indices = ExpandToIndices(listing, entries);

        if (!await RunEditAsync("MENUSTR_COPY_FILES2CLIPBOARD", "TEXT_STATUS_COPYING_OUT",
                (progress, token) => ArchiveExtractor.ExtractMembers(
                    listing.ArchivePath, indices, staging, CurrentPassword(listing.ArchivePath),
                    progress, token)))
        {
            return;
        }

        var topLevel = entries
            .Select(entry => Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();

        await SetClipboardFilesAsync(topLevel);
    }

    /// <summary>
    /// 从剪贴板粘贴文件（Ctrl+V）：剪贴板上有文件的路径就加进当前这一层。
    /// <para>剪贴板里是"文件"而不是"文本"这条路要走 OLE，拿不到（别的程序占着剪贴板、
    /// 或无桌面会话）时要说清楚，不能装作剪贴板是空的。</para>
    /// </summary>
    private async void PasteEntriesAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        IReadOnlyList<string> paths;
        if (Clipboard is not { } clipboard)
        {
            await ShowMessageAsync("MENUSTR_PASTE_FILESFROMCLIPBOARD", _lang.Get("TEXT_ERR_CLIPBOARD_UNAVAILABLE"));
            return;
        }

        try
        {
            paths = await ReadClipboardFilesAsync(clipboard);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("MENUSTR_PASTE_FILESFROMCLIPBOARD",
                _lang.Get("TEXT_ERR_CLIPBOARD_UNAVAILABLE") + ex.Message);
            return;
        }

        if (paths.Count == 0)
        {
            await ShowMessageAsync("MENUSTR_PASTE_FILESFROMCLIPBOARD", _lang.Get("TEXT_ERR_CLIPBOARD_EMPTY"));
            return;
        }

        await AddPathsToArchiveAsync(listing, paths);
    }

    /// <summary>复制文件完整路径：把选中项在包里的位置写成<strong>压缩包全路径 + 包内路径</strong>。
    /// 参考软件给的就是这一种形式（解压之后它会长在那条路径上），而不是一句 <c>a/b.txt</c>。</summary>
    private async void CopyPathsAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var entries = SelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }

        var lines = entries.Select(entry => Path.Combine(
            listing.ArchivePath,
            entry.Path.Replace('/', Path.DirectorySeparatorChar)));

        if (Clipboard is not { } clipboard)
        {
            await ShowMessageAsync("MENUSTR_COPY_FULL_PATHNAMES", _lang.Get("TEXT_ERR_CLIPBOARD_UNAVAILABLE"));
            return;
        }

        try
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("MENUSTR_COPY_FULL_PATHNAMES",
                _lang.Get("TEXT_ERR_CLIPBOARD_UNAVAILABLE") + ex.Message);
        }
    }

    /// <summary>
    /// 在进度窗口里跑一次就地改写。返回 true 表示真的改成了 —— 调用方据此决定要不要重开视图。
    /// <para>进度窗口的措辞由 <paramref name="statusKey"/> 给：改名要把那一个成员解出来再压回去，
    /// 说"正在改写压缩包"是实话但不够；其余几项一个字都合适。</para>
    /// </summary>
    private async Task<bool> RunEditAsync(
        string titleKey,
        string statusKey,
        Action<IProgress<ArchiveProgress>, CancellationToken> work)
    {
        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, statusKey,
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        var sink = progress.CreateSink();
        _ = progress.ShowDialog(this);

        try
        {
            await Task.Run(() => work(sink, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return false;
        }
        catch (ArchiveConstraintException ex)
        {
            progress.Close();
            await ShowMessageAsync(titleKey, ReasonText(ex.Reason, "TEXT_ERR_EDIT_FAILED"));
            return false;
        }
        catch (Exception ex)
        {
            progress.Close();
            await ShowMessageAsync(titleKey, _lang.Get("TEXT_ERR_EDIT_FAILED") + ex.Message);
            return false;
        }

        progress.Close();
        return true;
    }

    /// <summary>「这件事对这个输入做不到」→ 一句实话。映射表只有这一份 ——
    /// <c>NewArchiveWindow</c> 那边管创建那几种，这里管就地编辑与注释这几种。</summary>
    private string ReasonText(ArchiveConstraintReason reason, string fallbackKey) => reason switch
    {
        ArchiveConstraintReason.ArchiveNotEditable => _lang.Label("TEXT_ERR_ARCHIVE_NOT_EDITABLE"),
        ArchiveConstraintReason.DirectoryRenameNotSupported => _lang.Label("TEXT_ERR_DIRECTORY_RENAME"),
        ArchiveConstraintReason.ArchiveCommentNotSupported => _lang.Label("TEXT_ERR_COMMENT_NOT_SUPPORTED"),
        ArchiveConstraintReason.ArchiveCommentTooLong => _lang.Label("TEXT_ERR_COMMENT_TOO_LONG"),
        _ => _lang.Label(fallbackKey),
    };

    /// <summary>
    /// 改完重建视图：清单是从文件里读出来的那一份，改完不重读，屏幕上摆的就是旧账。
    /// <para>重建之后回到用户原来待的那一层 —— 删掉深层的一个文件就被扔回根目录，
    /// 是在惩罚他做了件对的事。</para>
    /// </summary>
    private void ReloadAfterEdit(string archivePath, string folderPath)
    {
        ArchiveListing listing;
        try
        {
            listing = ArchiveReader.Read(archivePath, CurrentPassword(archivePath));
        }
        catch (Exception ex)
        {
            // 改是改成了，但读不回来（包被别的程序换掉、口令失效）。这时不能继续显示旧清单 ——
            // 那是一份已经不存在的账。退回首页并把原因说出来。
            ShowHome();
            _ = new MessageWindow(_lang, _lang.Get("TEXT_TITLE_ERROR"),
                _lang.Get("TEXT_ERR_OPEN_ARCHIVE_FAILED") + ex.Message).ShowDialog(this);
            return;
        }

        ShowArchiveView(listing);
        _browser?.ShowFolder(folderPath);
    }

    private string CurrentFolderPath() => _browser?.CurrentFolderPath ?? string.Empty;

    /// <summary>
    /// 剪贴板落地用的临时目录。一次复制一个目录，名字里带上进程号与时间，互不覆盖。
    /// <para>顺手清掉<strong>七天前</strong>的那几个：这些文件在用户粘完之前都不能删，
    /// 但一直攒着会变成 %TEMP% 里的一堆孤儿。七天是"没人还会去粘"的保守线。</para>
    /// </summary>
    private static string ClipboardStagingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Andzip", "Clipboard");
        Directory.CreateDirectory(root);

        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var stale in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(stale) < cutoff)
                {
                    Directory.Delete(stale, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就算了：它是 %TEMP% 里的一堆字节，不该让一次复制变成失败。
            }
        }

        var fresh = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fresh);
        return fresh;
    }

    private async Task SetClipboardFilesAsync(IReadOnlyList<string> paths)
    {
        if (Clipboard is not { } clipboard)
        {
            await ShowMessageAsync("MENUSTR_COPY_FILES2CLIPBOARD", _lang.Get("TEXT_ERR_CLIPBOARD_UNAVAILABLE"));
            return;
        }

        var data = new DataTransfer();
        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await StorageProvider.TryGetFolderFromPathAsync(path)
                : await StorageProvider.TryGetFileFromPathAsync(path);

            if (item is not null)
            {
                data.Add(DataTransferItem.CreateFile(item));
            }
        }

        try
        {
            await clipboard.SetDataAsync(data);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("MENUSTR_COPY_FILES2CLIPBOARD",
                _lang.Get("TEXT_ERR_CLIPBOARD_UNAVAILABLE") + ex.Message);
        }
    }

    /// <summary>剪贴板上的文件路径。没有文件（纯文本、空的、别的格式）返回空表。</summary>
    private static async Task<IReadOnlyList<string>> ReadClipboardFilesAsync(IClipboard clipboard)
    {
        var transfer = await clipboard.TryGetDataAsync();
        if (transfer is null)
        {
            return [];
        }

        try
        {
            var files = await transfer.TryGetFilesAsync();
            return files is null
                ? []
                : [.. files
                    .Select(item => item.TryGetLocalPath() ?? item.Path?.LocalPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path!)];
        }
        finally
        {
            // 拿到的句柄要尽快还回去（它会锁着别的程序实现的那份数据），所以路径取完就放。
            transfer.Dispose();
        }
    }

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>另存为：把当前这个包<strong>原样复制</strong>一份到新名字。
    /// <para>不重压 —— 重压要重新选格式、级别、口令，那是「新建压缩文件」的活；
    /// 而且原包里引擎写不出来的东西（非常规方法、注释）重压一次就丢了。</para>
    /// </summary>
    private async void SaveArchiveAsAsync()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var choice = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _lang.Label("MENUSTR_SAVE_AS_ARCHIVE"),
            SuggestedFileName = Path.GetFileName(listing.ArchivePath),
            DefaultExtension = Path.GetExtension(listing.ArchivePath).TrimStart('.'),
        });

        var target = choice?.TryGetLocalPath();
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(target), listing.ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            await new MessageWindow(_lang, _lang.Get("MENUSTR_SAVE_AS_ARCHIVE"), _lang.Get("TEXT_ERR_SAVE_AS_SAME_PATH"))
                .ShowDialog(this);
            return;
        }

        try
        {
            File.Copy(listing.ArchivePath, target, overwrite: true);
            await ShowMessageAsync("MENUSTR_SAVE_AS_ARCHIVE", _lang.Format("TEXT_SAVE_AS_SUCCEEDED", ("path", target)));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("MENUSTR_SAVE_AS_ARCHIVE", _lang.Get("TEXT_ERR_SAVE_AS_FAILED") + ex.Message);
        }
    }

    /// <summary>删除压缩文件：移到回收站，还能反悔。</summary>
    private async void DeleteArchiveToRecycleBin() => await DeleteArchiveAsync(permanent: false);

    /// <summary>永久删除压缩文件：直接删，不经过回收站。</summary>
    private async void PermanentlyDeleteArchive() => await DeleteArchiveAsync(permanent: true);

    /// <summary>
    /// 删掉当前打开的包。两条路径的差别只在"能不能反悔"，所以确认文案也要把这件事说明白 ——
    /// 尤其是永久删除那条，它连一次反悔的机会都不给。
    /// <para>顺序要紧：<strong>先关视图再删</strong>。图标视图可能还开着包文件的句柄在读条目，
    /// 那时删会失败，而且失败原因（文件占用）跟用户的操作看起来毫无关系。</para>
    /// </summary>
    private async Task DeleteArchiveAsync(bool permanent)
    {
        if (_listing is not { } listing)
        {
            return;
        }

        var path = listing.ArchivePath;
        var confirmed = await MessageWindow.ConfirmAsync(
            this,
            _lang,
            _lang.Get(permanent ? "MENUSTR_PERM_DELETE_ARCHIVE" : "MENUSTR_DELETE_ARCHIVE"),
            _lang.Get(permanent ? "TEXT_CONFIRM_PERM_DELETE_ARCHIVE" : "TEXT_CONFIRM_DELETE_ARCHIVE")
                + Environment.NewLine + Path.GetFileName(path),
            "TEXT_BTN_DELETE");

        if (!confirmed)
        {
            return;
        }

        CloseArchive();

        try
        {
            if (permanent)
            {
                File.Delete(path);
            }
            else if (OperatingSystem.IsWindows())
            {
                RecycleBin.Delete(path);
            }
            else
            {
                // 没有回收站可去。这里不悄悄退化成永久删除 —— 那是把"还能反悔"的承诺
                // 在用户不知情的情况下换掉，比报错严重。
                await ShowMessageAsync("MENUSTR_DELETE_ARCHIVE", _lang.Get("TEXT_ERR_DELETE_FAILED") + "recycle bin");
                return;
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                permanent ? "MENUSTR_PERM_DELETE_ARCHIVE" : "MENUSTR_DELETE_ARCHIVE",
                _lang.Get("TEXT_ERR_DELETE_FAILED") + ex.Message);
        }
    }

    /// <summary>在资源管理器里打开这个包所在的位置并选中它。</summary>
    private void OpenArchiveFolder()
    {
        if (_listing is { } listing)
        {
            ArchiveReveal.Reveal(listing.ArchivePath);
        }
    }

    private Task ShowMessageAsync(string titleKey, string message)
        => new MessageWindow(_lang, _lang.Get(titleKey), message).ShowDialog(this);

    /// <summary>关闭压缩文件（F4）：回到首页。</summary>
    private void CloseArchive()
    {
        if (_listing is null)
        {
            return;
        }

        ShowHome();
    }

    // ---------- 命令 ----------

    /// <summary>
    /// 深色模式开关（「查看」菜单最后一条）。它写的就是「设置 → 查看 → 界面主题」那一个键，
    /// 两边永远同一件事 —— 只是这一条<strong>看得见</strong>。
    /// <para>切回的不是"浅色"而是"进深色之前那一档"（没进过深色就是跟随系统）：那一档有三个取值，
    /// 做成三态循环的话，用户第二次点下去猜不到会到哪。再点一次深色也不覆盖这份记忆 ——
    /// 深色 ↔ 原本那一档 是一个开关该有的样子。</para>
    /// <para>落盘之后走 <see cref="ApplyViewDefaults"/>，而不是自己 <c>Design.Apply</c>：
    /// 主题怎么生效只有一条路，两条路迟早长得不一样。</para>
    /// </summary>
    private void ToggleTheme()
    {
        if (Design.Mode == ThemeMode.Dark)
        {
            PersistTheme(_beforeDark);
            return;
        }

        _beforeDark = Design.Mode;
        PersistTheme(ThemeMode.Dark);
    }

    private void PersistTheme(ThemeMode mode)
    {
        _settings.SetString(SettingKeys.ViewTheme, mode.ToStorage());
        _settings.Save(AppSettings.DefaultFilePath);
        ApplyViewDefaults();
    }

    /// <summary>
    /// 「视图 → 置顶」：把这扇窗口钉在最前面，再点一次放开。
    /// <para>状态的<strong>唯一一份就是窗口自己那个 <c>Topmost</c></strong>，所以这里不再存一个
    /// <c>_topMost</c> 布尔 —— 存了就会有两个真相（菜单的勾照着布尔画，实际压不压得住照着窗口画，
    /// 而窗口可能被系统改动：最小化、别处的置顶窗口、另一扇 Andzip 窗口）。勾由
    /// <see cref="RefreshCommandStates"/> 从那一个真相算出来。</para>
    /// <para>不落盘（与 <see cref="ToggleTheme"/> 不同）：置顶是"这一阵子我先盯着这个窗口"的姿势，
    /// 不是设置。做成跨重启的，等于每次开机都挡一扇在前面，而那正是这一条要防的事。</para>
    /// <para>只钉这一扇：<c>Ctrl+Shift+N</c> 起的那一扇是新进程、新窗口，从没钉开始 ——
    /// 与资源管理器每个窗口各钉各的一个道理。自己的那些框（进度、确认、设置）都是
    /// <c>ShowDialog(this)</c>，被钉住的窗口盖不住自己的对话窗，所以不用跟着钉。</para>
    /// </summary>
    private void ToggleTopMost()
    {
        Topmost = !Topmost;
        RefreshCommandStates();
    }

    /// <summary>
    /// 「视图 → 列设置」勾 / 取消一列：改内存里那一份、落盘、<strong>当场</strong>重画。
    /// <para>重画走 <see cref="ArchiveView.SetHiddenColumns"/> 而不是重建整份视图 ——
    /// 关掉一列不该把用户送回根目录重找自己刚才在看的东西。</para>
    /// <para>存的是"关掉了哪些"，不是"显示着哪些"：新增一列时默认就是显示的，
    /// 老设置里没提到它，不用为每一列写一次迁移。首页上这一项是灰的（在
    /// <see cref="MainMenuModel.ArchiveOnlyCommands"/> 里），所以这里不必为"没开着包"另写一支。</para>
    /// </summary>
    private void SetColumnVisible(string key, bool visible)
    {
        if (visible)
        {
            _hiddenColumns.Remove(key);
        }
        else if (!_hiddenColumns.Contains(key))
        {
            _hiddenColumns.Add(key);
        }

        _settings.SetStringList(SettingKeys.HiddenColumns, _hiddenColumns);
        _settings.Save(AppSettings.DefaultFilePath);

        _browser?.SetHiddenColumns(_hiddenColumns);
    }

    /// <summary>
    /// 「视图 → 工具栏」换一档：落盘，然后<strong>重做一条</strong>工具栏。
    /// <para>不是只改 <c>IsVisible</c>：大与小差的不是显不显示，是每一格里画不画文字、
    /// 图标多大、上下内距多少 —— 那些都在构造时定下了。改档走的是与换主题同一条路
    /// （<see cref="BuildToolBar"/> 换掉 <c>_headerHost.Child</c>），所以两处只会有一处知道
    /// "一条工具栏怎么造出来"。</para>
    /// <para>同值直接返回：菜单每次展开都会重建那三行，点在当前那一档上是可能发生的事，
    /// 而重做一条工具栏会把它上面的悬停态与焦点一起丢掉 —— 没人要求却先动了一下。</para>
    /// </summary>
    private void SetToolBarMode(ToolBarMode mode)
    {
        if (mode == _toolBarMode)
        {
            return;
        }

        _toolBarMode = mode;
        _settings.SetString(SettingKeys.ViewToolBar, mode.ToStorage());
        _settings.Save(AppSettings.DefaultFilePath);

        _toolBar = BuildToolBar();
        _headerHost.Child = _toolBar;

        // 新那一条上每颗的 IsEnabled 都是构造初值，必须立刻按当前视图与选择重算一遍。
        RefreshCommandStates();
    }

    /// <summary>
    /// 「视图 → 图像预览」勾上 / 取消：落盘，然后<strong>就地</strong>改那一格。
    /// <para>不重建视图 —— 与「列设置」同一条理由：勾一格版面开关不该把用户当前所在的文件夹、
    /// 展开状态与选中项一起丢掉。而且这里更明显：重建会把选中清空，于是"勾上预览"这一步
    /// 恰好把唯一能触发预览的那样东西（选中的那一张图）取消了，用户会看到"点了没反应"。</para>
    /// <para>这一项与「置顶」相反，是<strong>要落盘的</strong>：那一格是版面，版面是设置。</para>
    /// </summary>
    private void TogglePreview()
    {
        _showPreview = !_showPreview;
        _settings.SetBool(SettingKeys.ViewPreview, _showPreview);
        _settings.Save(AppSettings.DefaultFilePath);

        _browser?.SetPreviewVisible(_showPreview);
        RefreshCommandStates();
    }

    /// <summary>
    /// 设置窗口（F5，FR-5）：模态，与参考软件一致。
    /// <para>关掉之后先<strong>从磁盘重读</strong>再重新应用一次「查看」那组设置。两步都必要：
    /// 设置窗口写盘用的是它自己那份实例，窗口手里这份还是启动时的旧快照 ——
    /// 不重读，① 这里应用的是旧值，用户点完「立即应用」界面不动；② 更糟的是窗口之后任何一次
    /// 写盘（记最近的文件、记住上次输出目录）都会拿旧快照把整个 <c>settings.ini</c> 覆盖掉，
    /// 刚才那一项设置连重启都找不回来。0.13.0 之前"自动展开树"设置不生效就是这么来的。</para>
    /// </summary>
    private void OpenSettingsDialog() => OpenSettingsDialogOn(null);

    /// <summary>
    /// 打开设置窗口。<paramref name="startPage"/> 是导航键 —— 工具栏那颗「切换语言」传语言页，
    /// 因为"换语言"这件事真正的开关在<strong>那一页</strong>（改完要重启才全面生效），
    /// 工具栏不该假装自己能换。键写错就是代码错：构造时直接抛，
    /// 而不是悄悄开在第一页让人以为按错了别的什么。
    /// </summary>
    private async void OpenSettingsDialogOn(string? startPage)
    {
        var dialog = new SettingsWindow(_lang, _settings, AppSettings.DefaultFilePath, startPage);
        await dialog.ShowDialog(this);
        _settings.Reload(AppSettings.DefaultFilePath);
        ApplyViewDefaults();

        // 树、状态栏、默认视图样式是压缩包视图内部的构造期决定，重开一次视图才看得到。
        // 但要看用户此刻停在哪一面：正看着首页就别把人扔进压缩包视图。
        if (_listing is not null && _browser is not null)
        {
            var wasShowingHome = ReferenceEquals(_contentHost.Content, _homeContent);
            ShowArchiveView(_listing);
            if (wasShowingHome)
            {
                ShowHome();
            }
        }
    }

    /// <summary>
    /// 「新建压缩文件」对话框（FR-2，模态，与参考软件一致）。
    /// <para>默认值取自「设置 → 压缩」；顺带把"上次用的输出目录"读给它、并让它压完写回来 ——
    /// 对话框自己不知道设置存在哪、什么时候落盘。</para>
    /// </summary>
    private void OpenNewArchiveDialog() => OpenNewArchiveDialog([]);

    /// <param name="seedPaths">拖进窗口的那批路径（或右键「压缩到…」那一条）。空列表 = 从菜单新建，
    /// 一切从空开始；一个路径以它命名，多个才叫「新建压缩」。</param>
    private void OpenNewArchiveDialog(IReadOnlyList<string> seedPaths)
    {
        var dialog = new NewArchiveWindow(
            _lang,
            CompressionDefaults.Load(_settings),
            seedPaths: seedPaths,
            lastOutputFolder: _settings.GetString(SettingKeys.CompressLastOutputFolder),
            rememberOutputFolder: RememberOutputFolder);
        _ = dialog.ShowDialog(this);
    }

    private void RememberOutputFolder(string directory)
    {
        if (directory.Length == 0
            || string.Equals(_settings.GetString(SettingKeys.CompressLastOutputFolder), directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.SetString(SettingKeys.CompressLastOutputFolder, directory);
        _settings.Save(AppSettings.DefaultFilePath);
    }

    /// <summary>打开压缩文件（Ctrl+O / 工具栏「打开」/ 文件菜单）：弹系统文件框选包。</summary>
    private async void OpenArchiveAsync()
    {
        var archivePath = await PickArchiveAsync();
        if (archivePath is not null)
        {
            await OpenArchive(archivePath);
        }
    }

    /// <summary>
    /// 读取清单并切到压缩包视图。加密包会在这里问密码 —— 清单本身就在包里，
    /// 所以「先试一次、要密码再问」比「先问一遍再试」少一次无谓的弹窗。
    /// </summary>
    public async Task OpenArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        ArchiveListing listing;
        // 上次为这个包输过的口令先顶上：清单要口令、解压也要，不记忆的话同一个包至少问两遍。
        var hasCached = Session.Passwords.TryGet(archivePath, out var cached);
        var password = hasCached ? cached : null;
        while (true)
        {
            try
            {
                listing = await Task.Run(() => ArchiveReader.Read(archivePath, password));
                break;
            }
            catch (ArchiveFailureException ex) when (ex.IsPassword)
            {
                // 缓存里那个口令不对（包换了口令）：忘掉它，别再拿同一个错口令去撞第二遍。
                Session.Passwords.Forget(archivePath);
                var prompt = new PasswordWindow(_lang, initialPassword: null, requireConfirmation: false);
                if (await prompt.ShowDialog<bool>(this) != true)
                {
                    return;
                }

                password = prompt.Password;
            }
            catch (ArchiveConstraintException)
            {
                var unsupported = new MessageWindow(_lang, _lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_ERR_OPEN_ARCHIVE_NOT_SUPPORTED"));
                _ = unsupported.ShowDialog(this);
                return;
            }
            catch (Exception ex)
            {
                var error = new MessageWindow(_lang, _lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_ERR_OPEN_ARCHIVE_FAILED") + ex.Message);
                _ = error.ShowDialog(this);
                return;
            }
        }

        if (password is { Length: > 0 })
        {
            Session.Passwords.Remember(archivePath, password);
        }

        ShowArchiveView(listing);
        RecordRecentArchive(archivePath);
    }

    // ---------- 文件 → 最近的文件 ----------

    /// <summary>
    /// 打开成功才记账。失败的包、被取消的口令都不该出现在「最近的文件」里 ——
    /// 那个菜单的承诺是"点开就能再看一次"。
    /// </summary>
    private void RecordRecentArchive(string archivePath)
    {
        RecentFiles.Record(_settings, archivePath);
        _settings.Save(AppSettings.DefaultFilePath);
    }

    /// <summary>菜单展开时读的那一份：先把已经不存在的记录清掉再给出去。</summary>
    private IReadOnlyList<string> LoadRecentFiles()
    {
        if (RecentFiles.Prune(_settings) > 0)
        {
            _settings.Save(AppSettings.DefaultFilePath);
        }

        return RecentFiles.Load(_settings);
    }

    private void OpenRecentFile(string path) => _ = OpenArchive(path);

    private void ClearRecentFiles()
    {
        RecentFiles.Clear(_settings);
        _settings.Save(AppSettings.DefaultFilePath);
    }

    /// <summary>
    /// 解压（Ctrl+E，也就是首页工具栏那颗「解压」）：5.0.0 起这一颗做的是<strong>与右键「解压」、
    /// 与双击那一档同一件事</strong> —— 先读一遍清单，落点由包里的形状决定（<c>ExtractionLayout</c>），
    /// 不再先开一扇「选择解压路径」问一句。用户的话是「所有的解压都是智能解压」。
    /// <para>
    /// 执行体是 <see cref="DirectExtract.ExtractAsync"/> 那<strong>同一份</strong>：口令、进度、重名询问、
    /// 失败报法全在里头。以前这里是一份平行的实现（自己的口令段、自己的 <c>ExtractionSettings</c>、
    /// 自己的错误框），两份代码两份命运 —— 4.8.0 那次"对话框不避让同名文件夹"就是这么来的。
    /// 要自己挑目录的走「文件 → 解压到…」（<see cref="ExtractToAsync"/>）。
    /// </para>
    /// </summary>
    private async void ExtractArchiveAsync()
    {
        var archivePath = _listing?.ArchivePath ?? await PickArchiveAsync();
        if (archivePath is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_EXTRACTING",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        _ = progress.ShowDialog(this);

        // 浏览这个包时已经输过口令 → 带过去，不再问第二遍（右键那一路没有这一份，传 null）。
        var cached = Session.Passwords.TryGet(archivePath, out var remembered) ? remembered : null;
        var run = await DirectExtract.ExtractAsync(
            _lang, _settings, ShellTask.Extract, archivePath, progress, cts, cached);

        // 解出来了才算这个口令是对的。一输完就存，代价是下一次开机 Andzip 会拿着一个
        // 从没验证过的口令直接失败 —— 用户看到的「密码错误」是他根本没输过的东西。
        if (run is { Succeeded: true, Prompted: true })
        {
            Session.Passwords.Remember(archivePath, run.Password);
        }

        // 「设置 → 解压 → 解压后打开文件夹」：只在真的解完了之后开。
        if (run is { Succeeded: true, Destination: { } folder }
            && ExtractionDefaults.Load(_settings).OpenFolderAfterExtract)
        {
            OpenInExplorer(folder);
        }

        progress.Close();
    }

    /// <summary>
    /// 解压到…（Ctrl+Shift+E）：开「选择解压路径」那扇对话框，由用户挑目录。
    /// <para>5.0.0 之前这就是「解压文件」的全部；那颗改成一键智能解压之后，这一份挪到这里，
    /// 因为对话框里那三样（挑目录、解压后删除压缩文件、记住设置）智能解压表达不了。
    /// 名字上两者不冲突：一条叫「解压」，一条叫「解压到…」—— 后者是"我自己说去哪儿"。</para>
    /// </summary>
    private async void ExtractToAsync()
    {
        var archivePath = _listing?.ArchivePath ?? await PickArchiveAsync();
        if (archivePath is null)
        {
            return;
        }

        var extractDialog = new ExtractPathWindow(_lang, archivePath);
        if (await extractDialog.ShowDialog<bool>(this) != true || extractDialog.Choice is not { } choice)
        {
            return;
        }

        // 复选项「解压到选定文件夹下与压缩包同名的文件夹」：勾着（默认）就在那个目录下再建一层。
        // 那一层是我们自己造的目录，所以走同一套避让 —— 名字被占用就依次试
        // 「名字1」「名字2」。以前这里是裸 Path.Combine：用户选的那个目录里只要已经有一个同名文件夹，
        // 这一次解压就静默并进那个文件夹里，把包里的内容和目录里原有的东西混在一处
        // （原有的同名文件还会撞上 FR-3.7 的逐个询问）。右键那一侧从 0.9.6 起就在避让，
        // 同一个"解压"两条路两种结局，这才是这条的成因（见开发记录第七节第 95 条）。
        var destination = choice.ExtractIntoArchiveNamedFolder
            ? ShellTasks.UniqueFolder(ShellTasks.SiblingFolder(archivePath, choice.DestinationPath))
            : choice.DestinationPath;

        // 加密压缩包：解压前弹出「输入密码」（§3.2 对话框 #7）。
        // 探测要打开归档读头部，放到后台线程，避免卡住 UI。
        string? password = null;
        var prompted = false;
        if (Session.Passwords.TryGet(archivePath, out var remembered))
        {
            // 打开这个包时已经输过一次（浏览清单就要口令），解压没必要再问一遍。
            password = remembered;
        }
        else if (await Task.Run(() => ArchiveExtractor.IsPasswordProtected(archivePath)))
        {
            var passwordDialog = new PasswordWindow(_lang, initialPassword: null, requireConfirmation: false);
            if (await passwordDialog.ShowDialog<bool>(this) != true)
            {
                return;
            }

            password = passwordDialog.Password;
            prompted = true;
        }

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_EXTRACTING",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        // 重名询问（FR-3.7「要解压的文件」）：以进度窗口为属主，在 UI 线程弹出并等待用户选择
        DuplicateDecision ResolveDuplicate(string fileName)
            => Dispatcher.UIThread.InvokeAsync<DuplicateDecision>(async () =>
            {
                var dialog = new DuplicateFileWindow(_lang, fileName);
                await dialog.ShowDialog<bool>(progress);
                return dialog.Decision;
            }).GetAwaiter().GetResult();

        var settings = new ExtractionSettings(
            archivePath,
            destination,
            OverwritePolicy.Ask,
            Password: password,
            DeleteArchiveAfterExtraction: choice.DeleteArchiveAfterExtraction,
            DuplicateResolver: ResolveDuplicate,
            Progress: progress.CreateSink());

        _ = progress.ShowDialog(this);

        try
        {
            await Task.Run(() => ArchiveExtractor.Extract(settings, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return;
        }
        catch (Exception ex)
        {
            progress.Close();
            var error = new MessageWindow(_lang, _lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_ERR_EXTRACT_FAILED") + ex.Message);
            _ = error.ShowDialog(this);
            return;
        }

        // 解出来了才算这个口令是对的。一输完就存，代价是下一次开机 Andzip 会拿着一个
        // 从没验证过的口令直接失败 —— 用户看到的「密码错误」是他根本没输过的东西。
        if (prompted)
        {
            Session.Passwords.Remember(archivePath, password);
        }

        var extractionDefaults = ExtractionDefaults.Load(_settings);

        // 「设置 → 解压 → 解压后打开文件夹」：只在真的解完了之后开，失败与取消都不该弹出一个
        // 装了一半的目录 —— 那比不打开更容易让人以为东西都在里面。
        if (extractionDefaults.OpenFolderAfterExtract)
        {
            OpenInExplorer(destination);
        }

        // 跑完就关。「设置 → 解压 → 解压完成后不关闭进度窗口」那一档 4.9.0 删了：
        // 它唯一的作用是把这一屏留在屏幕上，而留着的时候它永远写着「正在解压… 100%」，
        // 看着就是卡住 —— 4.8.1 修过那句话，但修不完一个不该存在的开关。
        // 现在窗口里这两颗（「解压」与「解压到…」）与右键、双击那一路同一个结局。
        progress.Close();
    }

    /// <summary>
    /// 完整性测试（FR-3.6）：整个包读一遍校验 CRC，但不落地任何文件。
    /// <para>
    /// 结果必须说出来 —— 用户点这个按钮要的就是那句「通过了」或「哪个文件坏了」，
    /// 静默关掉进度窗口等于什么都没做。引擎报的错里带成员名，原样转达，不替他概括成「包坏了」。
    /// </para>
    /// </summary>
    private async void TestArchiveAsync()
    {
        // 没打开包时先挑一个，与「解压」同一条路径 —— 按钮常亮却点了没反应才是真故障。
        var archivePath = _listing?.ArchivePath ?? await PickArchiveAsync();
        if (archivePath is null)
        {
            return;
        }

        string? password = null;
        var prompted = false;
        if (Session.Passwords.TryGet(archivePath, out var remembered))
        {
            password = remembered;
        }
        else if (await Task.Run(() => ArchiveExtractor.IsPasswordProtected(archivePath)))
        {
            var passwordDialog = new PasswordWindow(_lang, initialPassword: null, requireConfirmation: false);
            if (await passwordDialog.ShowDialog<bool>(this) != true)
            {
                return;
            }

            password = passwordDialog.Password;
            prompted = true;
        }

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_TESTING",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        string titleKey;
        string body;
        var broken = false;
        var verified = false;
        var sink = progress.CreateSink();

        // 进度窗口要<strong>先摆出来</strong>再开测：整个包读一遍 CRC 在照片包上是好几秒，
        // 而这几秒里用户唯一的凭据就是那个在动的条。少了这一句，界面就是"点了没反应，
        // 然后突然冒出一个结果框"。
        _ = progress.ShowDialog(this);

        try
        {
            var report = await Task.Run(
                () => ArchiveTester.TestAll(archivePath, password, sink, cts.Token), cts.Token);

            broken = !report.Passed;
            verified = report.Passed;
            titleKey = broken ? "TEXT_TITLE_TEST_FAILED" : "TEXT_TITLE_TEST_PASSED";
            // 通过的那一句不报成员数：没打开包时是先挑文件再测的，手上没有清单，
            // 报个数就得再读一遍目录。「全部通过」本身就是答案，数字是状态栏早就给过的东西。
            body = broken ? DescribeBroken(report) : _lang.Get("TEXT_TEST_PASSED");
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return;
        }
        catch (InvalidDataException ex)
        {
            // 数据真的坏了，可内核这句没点到清单里的某个成员（"corrupt archive headers" 那一类）：
            // 原样转达，不替他概括成「包坏了」。这仍然是"坏"，所以下面照样给修复那一颗按钮。
            broken = true;
            titleKey = "TEXT_TITLE_TEST_FAILED";
            body = ex.Message;
        }
        catch (Exception ex)
        {
            // 连读都没读成（缺文件、口令错、格式不支持）—— 那不是「包坏了」，别混为一谈，
            // 也别在这种包上劝人修复。
            titleKey = "TEXT_TITLE_TEST_FAILED";
            body = _lang.Get("TEXT_TEST_NOT_VERIFIED") + ex.Message;
        }

        progress.Close();

        // 「全部通过」是这一屏里唯一能证明口令正确的证据 —— 报「未通过」和报「根本没测成」
        // 都不算，后者连包都没打开（缺文件、口令错），把那个口令记下来只会让下次开机直接失败。
        if (prompted && verified)
        {
            Session.Passwords.Remember(archivePath, password);
        }

        // 坏的是 ZIP 时，"要不要修"就在这一屏问完：用户刚看完坏成员名单，让他自己去找
        // 「工具 → 修复压缩包」是把一件事拆成两件事。别的格式修不了（7z 的头是编码过的一整块），
        // 那一种就只有关闭 —— 给一颗注定失败的按钮比少一颗更糟。
        var canRepair = OffersRepair(archivePath, broken);

        if (!canRepair)
        {
            await new MessageWindow(_lang, _lang.Get(titleKey), body).ShowDialog(this);
            return;
        }

        if (await new MessageWindow(
                _lang, _lang.Get(titleKey), body, "TEXT_BTN_TRY_REPAIR", "TEXT_BTN_CLOSE")
                .ShowDialog<bool>(this))
        {
            await RepairArchiveAsync(archivePath, cameFromTest: true);
        }
    }

    /// <summary>
    /// 测出坏成员之后，这一屏要不要给「尝试修复…」那颗按钮。
    /// <para>两个条件都是"给了才有意义"：没坏就不用修；<strong>只有 ZIP 修得了</strong>
    /// （7z 的头是编码过的一整块，残缺后无从重建 —— 见 <c>TEXT_REPAIR_ZIP_ONLY</c>）。
    /// 在修不了的包上摆一颗点了只会说"修不了"的按钮，比不摆更糟。</para>
    /// </summary>
    internal static bool OffersRepair(string archivePath, bool broken)
        => broken && Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 坏成员名单 → 界面文案。只列前 6 个名字：这一屏是让人决定"要不要修"的，
    /// 不是让人抄一遍文件名的；名单再长，那句"另有 N 个没查完"已经把规模说清楚了。
    /// </summary>
    internal string DescribeBroken(ArchiveTestReport report)
    {
        var header = _lang.Format(
            "TEXT_TEST_BROKEN_HEADER",
            ("bad", report.Failures.Count.ToString()),
            ("total", report.Members.ToString()));

        var names = report.Failures.Take(6).Select(failure => "  " + failure.Name);
        var tail = report.Unexamined > 0
            ? new[] { _lang.Format("TEXT_TEST_AND_MORE", ("n", report.Unexamined.ToString())) }
            : [];

        return string.Join(Environment.NewLine, [header, .. names, .. tail]);
    }

    /// <summary>
    /// 修复压缩包（工具菜单，以及「测试结果 → 尝试修复」那条路）。<strong>先量，再问</strong>：
    /// 进度窗口里跑一遍结构扫描，骨架对得上就直接说"没发现问题"，对不上才把三种模式摆出来 ——
    /// 好包不该逼用户去猜该选哪个模式（猜错的代价是白等一次全量重压）。
    /// <para><paramref name="presetPath"/>：刚看完坏成员名单的人不该再被问一次"你要修哪个包"。</para>
    /// <para><paramref name="cameFromTest"/>：同一句"结构没问题"，从菜单进来和从测试结果进来意思
    /// 不一样 —— 后者已经知道数据坏了，再劝他"数据有没有坏要用测试"就是自相矛盾。</para>
    /// </summary>
    private async Task RepairArchiveAsync(string? presetPath = null, bool cameFromTest = false)
    {
        var archivePath = presetPath ?? _listing?.ArchivePath ?? await PickArchiveAsync();
        if (archivePath is null)
        {
            return;
        }

        // 修复做的是 ZIP 骨架的字节级重建（EOCD / 中央目录 / 局部头），别的容器没有这套结构。
        // 先说清楚，而不是弹一个注定失败的对话框。
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await new MessageWindow(_lang, _lang.Get("TEXT_TITLE_REPAIR"), _lang.Get("TEXT_REPAIR_ZIP_ONLY"))
                .ShowDialog(this);
            return;
        }

        var findings = await InspectStructureAsync(archivePath);
        if (findings is null)
        {
            return;
        }

        if (findings.Count == 0)
        {
            // 从测试结果过来时不能重复那句"数据有没有坏要用测试"：用户正是从那一步来的。
            await new MessageWindow(_lang, _lang.Get("TEXT_TITLE_REPAIR"), _lang.Get(
                cameFromTest ? "TEXT_REPAIR_HEALTHY_AFTER_TEST" : "TEXT_REPAIR_HEALTHY"))
                .ShowDialog(this);
            return;
        }

        var dialog = new RepairWindow(
            _lang, archivePath, ZipRepairer.SuggestOutputPath(archivePath), findings);
        if (await dialog.ShowDialog<bool>(this) != true)
        {
            return;
        }

        var mode = dialog.Mode;
        var output = dialog.OutputPath;

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_REPAIRING",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        ZipRepairResult result;
        var repairSink = progress.CreateSink();

        // 同「测试」那一条：进度窗口必须先摆出来。修复是这里最慢的一步（模式三要逐成员解压重压），
        // 之前它压根没显示过，用户看到的只有"点了菜单 → 卡几秒 → 弹结果"。
        _ = progress.ShowDialog(this);

        try
        {
            result = await Task.Run(
                () => ZipRepairer.Repair(archivePath, mode, output, repairSink, cts.Token),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return;
        }
        catch (Exception ex)
        {
            progress.Close();
            await new MessageWindow(_lang, _lang.Get("TEXT_TITLE_ERROR"),
                _lang.Get("TEXT_ERR_REPAIR_FAILED") + ex.Message).ShowDialog(this);
            return;
        }

        // 修完当场把新包读一遍："收回 N 个成员"是修复器自己的说法，能不能用要靠引擎读回来才算。
        var recheck = await RecheckRepairedAsync(result, progress, repairSink, cts);

        progress.Close();
        await new MessageWindow(
            _lang,
            _lang.Get("TEXT_TITLE_REPAIR_RESULT"),
            DescribeRepair(result) + (recheck.Length == 0 ? string.Empty : Environment.NewLine + recheck))
            .ShowDialog(this);

        // 故意不自动打开修好的包：用户先要看的是"救回了什么、丢了什么"，
        // 而不是被领进一个看起来完好、其实缺成员的窗口。
    }

    /// <summary>
    /// 在<strong>同一个</strong>进度框里复查修好的那一份，返回要拼在结果后面的一行。
    /// <para>新开一框会变成"点一次要关两个框"，所以这里只换措辞（<c>NextPhase</c>）：
    /// 修复刚跑完时进度框还写着「正在修复」，那时它其实在读回来验货。</para>
    /// <para>取消或读不成也要留一行字。静默少一行，用户读到的就只有修复器自己的成绩单。</para>
    /// </summary>
    private async Task<string> RecheckRepairedAsync(
        ZipRepairResult result,
        ProgressWindow progress,
        IProgress<ArchiveProgress> sink,
        CancellationTokenSource cancellation)
    {
        if (!result.ProducedAnything)
        {
            // 没生成新包，也就没什么可复查 —— 不报这一句，免得结果框多一行废话。
            return string.Empty;
        }

        progress.NextPhase("TEXT_STATUS_VERIFYING");
        try
        {
            var report = await Task.Run(
                () => ArchiveTester.TestAll(result.OutputPath, password: null, sink, cancellation.Token),
                cancellation.Token);

            return report.Passed
                ? _lang.Format("TEXT_REPAIR_RECHECK_PASSED", ("total", report.Members.ToString()))
                : _lang.Format("TEXT_REPAIR_RECHECK_BROKEN",
                    ("bad", report.Failures.Count.ToString()),
                    ("total", report.Members.ToString()));
        }
        catch (Exception ex)
        {
            return _lang.Get("TEXT_REPAIR_RECHECK_SKIPPED")
                + (ex is OperationCanceledException ? _lang.Get("TEXT_BTN_CANCEL") : ex.Message);
        }
    }

    /// <summary>
    /// 在进度窗口里跑一遍 ZIP 结构扫描，返回"扫到的问题"。
    /// <para>返回 <strong>空表</strong> = 骨架对得上；返回 <c>null</c> = 没测成（用户取消 / 文件读不了），
    /// 那时已经报过一次，调用方直接收工，不要再追问模式。</para>
    /// <para>扫描本身不报进度（它是分块读头，秒级），所以进度条是不确定态；
    /// 它在这里的作用是"这件事正在发生"的凭据，别让用户以为点没反应。</para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> InspectStructureAsync(string archivePath)
    {
        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_INSPECTING",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        _ = progress.ShowDialog(this);

        ZipMap map;
        try
        {
            map = await Task.Run(() => ZipScanner.Inspect(archivePath), cts.Token);
        }
        catch (Exception ex)
        {
            progress.Close();
            // 取消不算故障，不弹框；其余（文件被占用、路径没了）原样转达。
            if (ex is not OperationCanceledException)
            {
                await new MessageWindow(_lang, _lang.Get("TEXT_TITLE_ERROR"), ex.Message).ShowDialog(this);
            }

            return null;
        }

        progress.Close();

        // 扫描那段字节读是不看取消标志的（它没有可中断的点），所以这里补一次：
        // 用户按了取消，就不能接着往下问"要修哪种"。
        if (cts.IsCancellationRequested)
        {
            return null;
        }

        return map.Damaged ? map.Notes : [];
    }

    /// <summary>修复结果的文案。名单截到 12 条 —— MessageWindow 按内容长高，
    /// 一个几万成员的包会把窗口撑到屏幕外，那等于没有报告。</summary>
    private string DescribeRepair(ZipRepairResult result)
    {
        var lines = new List<string>();

        if (!result.ProducedAnything)
        {
            lines.Add(_lang.Get("TEXT_REPAIR_NOTHING"));
        }
        else
        {
            lines.Add(_lang.Format("TEXT_REPAIR_RESULT",
                ("recovered", result.RecoveredEntries.Count.ToString()),
                ("skipped", result.SkippedEntries.Count.ToString())));
            lines.Add(result.OutputPath);
        }

        AppendNames(lines, _lang.Get("TEXT_REPAIR_RECOVERED"), result.RecoveredEntries);
        AppendNames(lines, _lang.Get("TEXT_REPAIR_SKIPPED"), result.SkippedEntries);

        foreach (var note in result.Notes.Take(12))
        {
            lines.Add("· " + note);
        }

        if (result.Notes.Count > 12)
        {
            lines.Add($"…（还有 {result.Notes.Count - 12} 条说明）");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendNames(List<string> lines, string label, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        var shown = names.Take(12).ToList();
        var suffix = names.Count > shown.Count ? $"… (+{names.Count - shown.Count})" : string.Empty;
        lines.Add($"{label}：{string.Join("、", shown)}{suffix}");
    }

    /// <summary>用系统资源管理器打开一个目录。打不开就算了，解压本身已经成功了。</summary>
    private static void OpenInExplorer(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 没有外壳可跳（精简系统 / 无桌面会话）时静默失败：解压的结果已经在磁盘上了，
            // 不该因为开不了窗口就报一个"解压失败"。
        }
    }

    private async Task<string?> PickArchiveAsync()
    {
        // 过滤器里的扩展名来自引擎（它报告自己能读的所有扩展名），不在 GUI 里抄一份。
        // 只给这一项：选择框因此只能选中受支持的格式。没有扩展名或扩展名写错的包仍然能打开，
        // 但走的是命令行参数这条入口（格式由内容嗅探，不看后缀）。
        var patterns = ArchiveFormatOption.OpenableExtensions
            .Select(extension => "*" + extension)
            .ToList();

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _lang.Label("MENUSTR_OPEN"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_lang.Get("TEXT_ARCHIVE_FILES")) { Patterns = patterns },
            ],
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void RegisterShortcuts(IReadOnlyDictionary<string, Action> commands)
    {
        foreach (var menu in MainMenuModel.Create())
        {
            foreach (var item in menu.Items)
            {
                if (!item.Enabled || item.CommandId is null || !commands.TryGetValue(item.CommandId, out var action))
                {
                    continue;
                }

                var gesture = GestureConverter.ToGesture(item.Gesture);
                if (gesture is null)
                {
                    continue;
                }

                KeyBindings.Add(new KeyBinding
                {
                    Gesture = gesture,
                    Command = new DelegateCommand(action),
                });
            }
        }
    }
}
