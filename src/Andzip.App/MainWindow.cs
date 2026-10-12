using System.Diagnostics;
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
        Background = new SolidColorBrush(Color.Parse("#208CFB"));

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
            [MainMenuModel.CommandToggleStartScreen] = ToggleStartScreen,
            [MainMenuModel.CommandSettings] = OpenSettingsDialog,
        };

        var root = new DockPanel();

        var menuBar = MainMenuBar.Create(_lang, commands);
        DockPanel.SetDock(menuBar, Dock.Top);
        root.Children.Add(menuBar);

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
    }

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
    }

    private void ShowArchiveView(ArchiveListing listing)
    {
        _browser?.Dispose();
        _browser = new ArchiveView(_lang, listing, _view);
        _listing = listing;
        _contentHost.Content = _browser.Root;
        Title = ArchiveTitle(listing.ArchivePath);
    }

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
    /// <para>关掉之后重新应用一次「查看」那组设置 —— 不然用户勾掉工具栏要重启才看得见效果，
    /// 而那看起来就像设置没生效。</para>
    /// </summary>
    private async void OpenSettingsDialog()
    {
        var dialog = new SettingsWindow(_lang, _settings, AppSettings.DefaultFilePath);
        await dialog.ShowDialog(this);
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
    private void OpenNewArchiveDialog()
    {
        var dialog = new NewArchiveWindow(
            _lang,
            CompressionDefaults.Load(_settings),
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
