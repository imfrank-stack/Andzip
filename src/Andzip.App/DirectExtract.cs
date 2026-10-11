using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Andzip.App;

/// <summary>
/// 没有主窗口的解压执行体：右键「直接解压到包名文件夹」/「解压到当前文件夹」，
/// 以及双击 + 「常规 → 双击 = 直接解压」。由注册表动词带上 <c>/task=</c> 拉起来
/// （见 <see cref="Services.FileAssociationService"/>）。
/// <para>
/// 刻意<strong>不</strong>创建主窗口：用户点的是进度条，跑完自己消失，
/// 中间插一张主窗口既挡路又多余。因此调用方把生命周期切成 OnExplicitShutdown，
/// 结束时代码自己 <c>Shutdown()</c> —— 没有主窗口，默认的生命周期策略会立刻退出进程。
/// </para>
/// <para>落盘位置由 <see cref="ShellTasks.ExtractDestination"/> 决定，与「选择解压路径」里
/// 默认勾选的那一项一致，不额外发明第二套规则。</para>
/// </summary>
internal static class DirectExtract
{
    public static async Task RunAsync(
        LangCatalog lang,
        AppSettings settings,
        string archivePath,
        string destination,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            lang,
            cts,
            "TEXT_STATUS_EXTRACTING",
            cancelViaEsc: settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true),
            standalone: true);
        progress.Show();

        try
        {
            await ExtractAsync(lang, archivePath, destination, progress, cts);
        }
        catch (Exception ex)
        {
            await ShowError(lang, progress, ex.Message);
        }
        finally
        {
            progress.Close();
            desktop.Shutdown();
        }
    }

    private static async Task ExtractAsync(
        LangCatalog lang,
        string archivePath,
        string destination,
        ProgressWindow progress,
        CancellationTokenSource cts)
    {
        // 加密包先问一次口令再开工：解压到一半才报“要口令”会留下一堆半截文件。
        string? password = null;
        if (await Task.Run(() => ArchiveExtractor.IsPasswordProtected(archivePath)))
        {
            var prompt = new PasswordWindow(lang, initialPassword: null, requireConfirmation: false);
            if (await prompt.ShowDialog<bool>(progress) != true)
            {
                return;
            }

            password = prompt.Password;
        }

        var settings = new ExtractionSettings(
            archivePath,
            destination,
            OverwritePolicy.Ask,
            Password: password,
            DuplicateResolver: fileName => AskAboutDuplicate(lang, progress, fileName),
            // UI 线程构造，因此引擎工作线程上报的进度会自动投递回 UI 线程。
            Progress: new Progress<ArchiveProgress>(progress.Update));

        try
        {
            await Task.Run(() => ArchiveExtractor.Extract(settings, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：静默收尾，不弹错误框吓唬人。
        }
        catch (Exception ex)
        {
            await ShowError(lang, progress, ex.Message);
        }
    }

    /// <summary>重名询问（FR-3.7）：以进度窗口为宿主，在工作线程回调里同步等 UI 答复。</summary>
    private static DuplicateDecision AskAboutDuplicate(LangCatalog lang, Window owner, string fileName)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new DuplicateFileWindow(lang, fileName);
            await dialog.ShowDialog<bool>(owner);
            return dialog.Decision;
        }).GetAwaiter().GetResult();

    private static Task ShowError(LangCatalog lang, Window owner, string message)
        => new MessageWindow(lang, lang.Get("TEXT_TITLE_ERROR"), lang.Get("TEXT_ERR_EXTRACT_FAILED") + message)
            .ShowDialog(owner);
}
