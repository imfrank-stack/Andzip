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
    private const string BaseTitle = "Andzip (Professional)";

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
    private readonly Control _homeContent;
    private readonly Control _toolBar;

    /// <summary>菜单条。留着是为了按命令找到某几项、在没有包可操作时把它们置灰。</summary>
    private readonly Menu _menuBar;
    private ArchiveView? _browser;
    private ArchiveListing? _listing;
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

        // 窗口底色回到白：以前整窗是品牌蓝，蓝色从工具栏的缝隙里漏出来，
        // 内容区一有留白就变成"蓝底上贴了张表"。现在蓝只待在工具栏那一条里（规范 5.1）。
        Background = Design.Surface.Base;
        FontFamily = Design.Font;
        FontSize = Design.Type.Body;

        _homeContent = HomeView.Create(lang);

        var commands = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            [MainMenuModel.CommandExit] = Close,
            [MainMenuModel.CommandNewArchive] = OpenNewArchiveDialog,
            [MainMenuModel.CommandOpenArchive] = OpenArchiveAsync,
            [MainMenuModel.CommandCloseArchive] = CloseArchive,
            [MainMenuModel.CommandExtractArchive] = ExtractArchiveAsync,
            [MainMenuModel.CommandTestArchive] = TestArchiveAsync,
            [MainMenuModel.CommandRepairArchive] = RepairArchiveAsync,
            [MainMenuModel.CommandSaveArchiveAs] = SaveArchiveAsAsync,
            [MainMenuModel.CommandDeleteArchive] = DeleteArchiveToRecycleBin,
            [MainMenuModel.CommandPermanentDeleteArchive] = PermanentlyDeleteArchive,
            [MainMenuModel.CommandOpenArchiveFolder] = OpenArchiveFolder,
            [MainMenuModel.CommandToggleStartScreen] = ToggleStartScreen,
            [MainMenuModel.CommandSettings] = OpenSettingsDialog,
        };

        var root = new DockPanel();

        _menuBar = MainMenuBar.Create(_lang, commands, new RecentFilesMenu(
            LoadRecentFiles, OpenRecentFile, ClearRecentFiles));

        // 菜单与工具栏之间那条 1px 线是层级的一部分：SimpleTheme 用不了阴影，
        // 层级就靠底色差和这根线表达（规范 5.1）。
        // 注意 Dock.Top 必须设在<strong>这层 Border</strong>上 —— 设在 _menuBar 上没用，
        // DockPanel 只看自己直接子元素的值，那样菜单栏会变成左边一整列。
        var menuHost = new Border
        {
            Child = _menuBar,
            Background = Design.Surface.Base,
            BorderBrush = Design.Line.Subtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(menuHost, Dock.Top);
        root.Children.Add(menuHost);

        // 「查看」= 参考软件的预览面板，尚未实现，所以这里不给命令（按钮呈禁用态）。
        _toolBar = MainToolBar.Create(_lang, new MainToolBarActions(
            OpenArchive: OpenArchiveAsync,
            ExtractArchive: ExtractArchiveAsync,
            NewArchive: OpenNewArchiveDialog,
            TestArchive: TestArchiveAsync));
        DockPanel.SetDock(_toolBar, Dock.Top);
        root.Children.Add(_toolBar);

        root.Children.Add(_contentHost);

        Content = root;
        ShowHome();
        ApplyViewDefaults();
        RegisterShortcuts(commands);
        AcceptFileDrops();
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
        _contentHost.Content = _homeContent;
        Title = EngineTitle;
        SetArchiveOnlyCommandsEnabled(false);
    }

    private void ShowArchiveView(ArchiveListing listing)
    {
        _browser?.Dispose();
        _browser = new ArchiveView(_lang, listing, _view);
        _listing = listing;
        _contentHost.Content = _browser.Root;
        Title = ArchiveTitle(listing.ArchivePath);
        SetArchiveOnlyCommandsEnabled(true);
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
    private async void OpenSettingsDialog()
    {
        var dialog = new SettingsWindow(_lang, _settings, AppSettings.DefaultFilePath);
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
        var sink = progress.CreateSink();
        try
        {
            await Task.Run(() => ArchiveTester.Test(archivePath, password, sink, cts.Token), cts.Token);

            titleKey = "TEXT_TITLE_TEST_PASSED";
            // 不报成员数：没打开包时是先挑文件再测的，手上没有清单，报个数就得再读一遍目录。
            // 「全部通过」本身就是答案，数字是状态栏早就给过的东西。
            body = _lang.Get("TEXT_TEST_PASSED");
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return;
        }
        catch (InvalidDataException ex)
        {
            // 数据真的坏了（CRC 对不上 / 解压失败）：引擎点到谁就是谁。
            titleKey = "TEXT_TITLE_TEST_FAILED";
            body = ex.Message;
        }
        catch (Exception ex)
        {
            // 连读都没读成（缺文件、口令错、格式不支持）—— 那不是「包坏了」，别混为一谈。
            titleKey = "TEXT_TITLE_TEST_FAILED";
            body = _lang.Get("TEXT_TEST_NOT_VERIFIED") + ex.Message;
        }

        progress.Close();
        await new MessageWindow(_lang, _lang.Get(titleKey), body).ShowDialog(this);
    }

    /// <summary>
    /// 修复压缩包（工具菜单）。先问模式，再跑，再把「收回 / 跳过 / 说明」三件事原样摊开 ——
    /// 修复是个允许失败的操作，报喜不报忧比不修更糟。
    /// </summary>
    private async void RepairArchiveAsync()
    {
        var archivePath = _listing?.ArchivePath ?? await PickArchiveAsync();
        if (archivePath is null)
        {
            return;
        }

        var dialog = new RepairWindow(_lang, archivePath, ZipRepairer.SuggestOutputPath(archivePath));
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

        progress.Close();
        await new MessageWindow(_lang, _lang.Get("TEXT_TITLE_REPAIR_RESULT"), DescribeRepair(result))
            .ShowDialog(this);

        // 故意不自动打开修好的包：用户先要看的是"救回了什么、丢了什么"，
        // 而不是被领进一个看起来完好、其实缺成员的窗口。
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
