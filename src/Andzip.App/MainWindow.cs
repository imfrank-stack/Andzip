using System.Diagnostics;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
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
    /// 而界面上没有一处写着版本 —— 谁也没法从一张图判断它是谁。多这一段，每张截图自己报户口。</para>
    /// <para>值来自 <c>build.ps1</c> 注入的 <c>-p:Version</c>；绕过它直接 <c>dotnet build</c>
    /// 会看到 csproj 里那个兜底版本，那正说明手上这份不是发布产物。
    /// <strong>读的是 <c>AssemblyName.Version</c> 而不是 <c>InformationalVersion</c></strong>：
    /// 后者会被 SourceLink 追加提交号（实测标题一度长成 "v1.9.1+44.3e846cf039…"），
    /// 那是流水线要看的东西，写在窗口上只是噪音。</para>
    /// </summary>
    private static string BaseTitle { get; } = $"Andzip (Professional) {AppVersion}";

    private static string AppVersion => typeof(MainWindow).Assembly.GetName().Version is { } v
        ? $"v{v.Major}.{v.Minor}.{v.Build}"
        : "v0.0.0";

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

    /// <summary>自绘标题栏（图标 + 菜单 + 版本号 + 三个窗口按钮，一行）。换主题时要重取一遍令牌。</summary>
    private readonly TitleBar _titleBar;

    /// <summary>整块窗口那张圆角阴影面板。它的底色就是"表面"，最大化时收掉圆角与边距。</summary>
    private readonly Border _surface;

    /// <summary>工具栏外面那层（那根发丝线归它画）。</summary>
    private readonly Border _headerHost;

    /// <summary>标题栏右端那一小截版本号。截图要能自报身份，靠的就是它。</summary>
    private readonly TextBlock _versionLabel;
    private ArchiveView? _browser;
    private ArchiveListing? _listing;
    private FindWindow? _find;
    private readonly AppSettings _settings;

    /// <summary>「设置 → 查看」的当前值；设置对话框关掉之后会重新读一次并应用。</summary>
    private ViewDefaults _view;

    /// <summary>「设置 → 高级 → 标题栏显示完整路径」。</summary>
    private bool _titleShowsFullPath;

    public MainWindow(LangCatalog lang, AppSettings settings)
    {
        _lang = lang;
        _settings = settings;
        _view = ViewDefaults.Load(settings);

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

        var commands = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            [MainMenuModel.CommandExit] = Close,
            [MainMenuModel.CommandNewArchive] = OpenNewArchiveDialog,
            [MainMenuModel.CommandOpenArchive] = OpenArchiveAsync,
            [MainMenuModel.CommandCloseArchive] = CloseArchive,
            [MainMenuModel.CommandExtractArchive] = ExtractArchiveAsync,
            [MainMenuModel.CommandTestArchive] = TestArchiveAsync,
            [MainMenuModel.CommandRepairArchive] = () => RepairArchiveAsync(),
            [MainMenuModel.CommandSaveArchiveAs] = SaveArchiveAsAsync,
            [MainMenuModel.CommandDeleteArchive] = DeleteArchiveToRecycleBin,
            [MainMenuModel.CommandPermanentDeleteArchive] = PermanentlyDeleteArchive,
            [MainMenuModel.CommandOpenArchiveFolder] = OpenArchiveFolder,
            [MainMenuModel.CommandToggleStartScreen] = ToggleStartScreen,
            [MainMenuModel.CommandSettings] = OpenSettingsDialog,
            [MainMenuModel.CommandFindFiles] = OpenFindDialog,
        };

        var root = new DockPanel();

        _menuBar = MainMenuBar.Create(_lang, commands, new RecentFilesMenu(
            LoadRecentFiles, OpenRecentFile, ClearRecentFiles));

        // 「查看」= 参考软件的预览面板，尚未实现，所以这里不给命令（按钮呈禁用态）。
        _toolBar = BuildToolBar();

        // 一行装完：图标 + 菜单 ……… 版本号 + 最小化 / 最大化 / 关闭。
        // 以前这是两行（标题文字单独一行，菜单第二行），用户的话是"上面的文字去掉，第二行整体上移"。
        // 版本号不删，只是从标题里挪到这一行的右端 —— 截图要能自报身份（1.9.1 定的规矩），
        // 而窗口自己的 Title 照旧带着引擎信息给任务栏、Alt+Tab 与读屏器读。
        _versionLabel = new TextBlock
        {
            Text = AppVersion,
            FontSize = Design.Type.Caption,
            Foreground = Design.Ink.Secondary,
            Margin = new Thickness(0, 0, Design.Space.X1, 0),
        };

        _titleBar = new TitleBar(this, _lang, resizable: true, leading: _menuBar, trailing: _versionLabel);
        DockPanel.SetDock(_titleBar, Dock.Top);
        root.Children.Add(_titleBar);

        // 工具栏与内容之间只留一根发丝线。
        // 注意 Dock.Top 必须设在<strong>这层 Border</strong>上 —— 设在工具栏自己身上没用，
        // DockPanel 只看自己直接子元素的值（1.4.0 就踩过：那样菜单栏会变成左边一整列）。
        _headerHost = new Border
        {
            Child = _toolBar,
            BorderBrush = Design.Line.Hair,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(Design.Space.X2, 0, Design.Space.X2, 0),
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
        RegisterShortcuts(commands);
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
        _headerHost.BorderBrush = Design.Line.Hair;
        _titleBar.Repaint();
        _menuBar.Background = Brushes.Transparent;
        _menuBar.Foreground = Design.Ink.Primary;

        // 工具栏整条重做：它的图标与文字颜色是构造时取的一次，光刷卡片底色救不回来。
        _toolBar = BuildToolBar();
        _headerHost.Child = _toolBar;

        // 版本号那一小截也是构造时取的颜色，跟着重取一次令牌。
        _versionLabel.Foreground = Design.Ink.Secondary;

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
    /// 按当前调色板做一条新工具栏。「查看 → 显示工具栏」的开关状态在这里一并带上，
    /// 否则换完主题工具栏会悄悄跑回来（<see cref="ApplyViewDefaults"/> 那次已经过去了）。
    /// </summary>
    private Control BuildToolBar()
    {
        var bar = MainToolBar.Create(_lang, new MainToolBarActions(
            OpenArchive: OpenArchiveAsync,
            ExtractArchive: ExtractArchiveAsync,
            NewArchive: OpenNewArchiveDialog,
            TestArchive: TestArchiveAsync,
            SwitchLanguage: () => OpenSettingsDialogOn("TEXT_SETTINGS_LANGUAGE")));

        // 「查看 → 显示工具栏」要跟着带上，否则换完主题工具栏会悄悄跑回来
        // （ApplyViewDefaults 那一次已经过去了）。
        bar.IsVisible = _view.ShowToolBar;
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
        _toolBar.IsVisible = _view.ShowToolBar;
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
        SetArchiveOnlyCommandsEnabled(false);
    }

    private void ShowArchiveView(ArchiveListing listing)
    {
        _browser?.Dispose();
        _browser = new ArchiveView(_lang, listing, _view);
        _listing = listing;
        CloseFindWindow();
        _contentHost.Content = _browser.Root;
        Title = ArchiveTitle(listing.ArchivePath);
        SetArchiveOnlyCommandsEnabled(true);
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

    // ---------- 文件 → 另存为 / 删除 / 永久删除 / 打开位置 ----------

    /// <summary>
    /// 按命令把"只有开着压缩包才能用"的那几项点亮或置灰。
    /// <para>用 <c>Tag</c>（<see cref="MainMenuBar"/> 建的时候写进 CommandId）而不是另存一份
    /// 控件引用：菜单结构改了不用回来改这张表，也不会漏掉新加的一项。</para>
    /// </summary>
    private void SetArchiveOnlyCommandsEnabled(bool enabled)
    {
        foreach (var item in WalkItems(_menuBar))
        {
            if (item.Tag is string command && MainMenuModel.ArchiveOnlyCommands.Contains(command))
            {
                item.IsEnabled = enabled;
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
    /// 另存为：把当前这个包<strong>原样复制</strong>一份到新名字。
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

    /// <summary>首页 / 压缩包视图切换（F6，FR-1.5）；未打开压缩包时保持首页。</summary>
    private void ToggleStartScreen()
    {
        if (_browser is null || _listing is null)
        {
            return;
        }

        if (ReferenceEquals(_contentHost.Content, _homeContent))
        {
            _contentHost.Content = _browser.Root;
            Title = ArchiveTitle(_listing.ArchivePath);
        }
        else
        {
            _contentHost.Content = _homeContent;
            Title = EngineTitle;
        }
    }

    // ---------- 命令 ----------

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

    /// <summary>解压文件（Ctrl+E）：解压已打开的压缩包；未打开时先选择压缩包（FR-3.1 流程）。</summary>
    private async void ExtractArchiveAsync()
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

        // 复选项「解压到选定文件夹下与压缩包同名的文件夹」（默认勾选）
        var destination = choice.ExtractIntoArchiveNamedFolder
            ? Path.Combine(choice.DestinationPath, Path.GetFileNameWithoutExtension(archivePath))
            : choice.DestinationPath;

        // 加密压缩包：解压前弹出「输入密码」（§3.2 对话框 #7）。
        // 探测要打开归档读头部，放到后台线程，避免卡住 UI。
        string? password = null;
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
            Session.Passwords.Remember(archivePath, password);
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

        var extractionDefaults = ExtractionDefaults.Load(_settings);

        // 「设置 → 解压 → 解压后打开文件夹」：只在真的解完了之后开，失败与取消都不该弹出一个
        // 装了一半的目录 —— 那比不打开更容易让人以为东西都在里面。
        if (extractionDefaults.OpenFolderAfterExtract)
        {
            OpenInExplorer(destination);
        }

        if (!extractionDefaults.KeepProgressWindow)
        {
            progress.Close();
        }
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
            Session.Passwords.Remember(archivePath, password);
        }

        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            _lang, cts, "TEXT_STATUS_TESTING",
            cancelViaEsc: _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));

        string titleKey;
        string body;
        var broken = false;
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
