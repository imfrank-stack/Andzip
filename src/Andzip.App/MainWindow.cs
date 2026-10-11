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

    private readonly LangCatalog _lang;
    private readonly ContentControl _contentHost = new();
    private readonly Control _homeContent;
    private ArchiveView? _browser;
    private ArchiveListing? _listing;
    private readonly AppSettings _settings;

    public MainWindow(LangCatalog lang, AppSettings settings)
    {
        _lang = lang;
        _settings = settings;

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
            [MainMenuModel.CommandToggleStartScreen] = ToggleStartScreen,
            [MainMenuModel.CommandSettings] = OpenSettingsDialog,
        };

        var root = new DockPanel();

        var menuBar = MainMenuBar.Create(_lang, commands);
        DockPanel.SetDock(menuBar, Dock.Top);
        root.Children.Add(menuBar);

        // 「查看」= 参考软件的预览面板，尚未实现，所以这里不给命令（按钮呈禁用态）。
        var toolBar = MainToolBar.Create(_lang, new MainToolBarActions(
            OpenArchive: OpenArchiveAsync,
            ExtractArchive: ExtractArchiveAsync,
            NewArchive: OpenNewArchiveDialog));
        DockPanel.SetDock(toolBar, Dock.Top);
        root.Children.Add(toolBar);

        root.Children.Add(_contentHost);

        Content = root;
        ShowHome();
        RegisterShortcuts(commands);
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
        _browser = new ArchiveView(_lang, listing);
        _listing = listing;
        _contentHost.Content = _browser.Root;
        Title = $"{Path.GetFileName(listing.ArchivePath)} - {BaseTitle}";
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
            Title = $"{Path.GetFileName(_listing.ArchivePath)} - {BaseTitle}";
        }
        else
        {
            _contentHost.Content = _homeContent;
            Title = EngineTitle;
        }
    }

    // ---------- 命令 ----------

    /// <summary>设置窗口（F5，FR-5）：模态，与参考软件一致。</summary>
    private void OpenSettingsDialog()
    {
        var dialog = new SettingsWindow(_lang, _settings, AppSettings.DefaultFilePath);
        _ = dialog.ShowDialog(this);
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
        string? password = null;
        while (true)
        {
            try
            {
                listing = await Task.Run(() => ArchiveReader.Read(archivePath, password));
                break;
            }
            catch (ArchiveFailureException ex) when (ex.IsPassword)
            {
                var prompt = new PasswordWindow(_lang, initialPassword: password, requireConfirmation: false);
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
        if (await Task.Run(() => ArchiveExtractor.IsPasswordProtected(archivePath)))
        {
            var passwordDialog = new PasswordWindow(_lang, initialPassword: null, requireConfirmation: false);
            if (await passwordDialog.ShowDialog<bool>(this) != true)
            {
                return;
            }

            password = passwordDialog.Password;
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
            // UI 线程构造，因此引擎工作线程上报的进度会自动投递回 UI 线程。
            Progress: new Progress<ArchiveProgress>(progress.Update));

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

        progress.Close();
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
            Title = MnemonicText.Strip(_lang.Get("MENUSTR_OPEN")),
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
