using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Andzip.App;

/// <summary>
/// 没有主窗口的解压执行体：右键「解压」/「解压并删除」，以及双击 + 「解压 → 双击 = 直接解压」。
/// 由注册表动词带上 <c>/task=</c> 拉起来（见 <see cref="Services.FileAssociationService"/>）。
/// <para>
/// 刻意<strong>不</strong>创建主窗口：用户点的是进度条，跑完自己消失，
/// 中间插一张主窗口既挡路又多余。因此调用方把生命周期切成 OnExplicitShutdown，
/// 结束时代码自己 <c>Shutdown()</c> —— 没有主窗口，默认的生命周期策略会立刻退出进程。
/// </para>
/// <para>
/// 落点取决于<strong>包里装着什么</strong>（<see cref="ExtractionLayout"/>），所以这一步排在口令之后 ——
/// 清单本身也在包里。0.9.12 之前这里还有一条"只看路径"的落点（解压到当前文件夹），
/// 两条并成一条之后不再有分支。
/// </para>
/// </summary>
internal static class DirectExtract
{
    public static async Task RunAsync(
        LangCatalog lang,
        AppSettings appSettings,
        ShellTask task,
        string archivePath,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            lang,
            cts,
            "TEXT_STATUS_EXTRACTING",
            cancelViaEsc: appSettings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true),
            standalone: true);
        progress.Show();

        try
        {
            await ExtractAsync(lang, appSettings, task, archivePath, progress, cts);
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

    /// <summary>
    /// 跑一次解压。<strong>三条路共用这一份</strong>：右键「解压」/「解压并删除」、双击那一档、
    /// 以及 5.0.0 起首页那颗「解压」（<c>MainWindow.ExtractArchiveAsync</c>）。
    /// <para>差别只在传进来的东西上：属主窗口（进度框挂在谁身上）、口令（主窗口浏览过这个包
    /// 就带着已验证的那一份来，不再问一遍）、以及要不要跑完删包。</para>
    /// </summary>
    /// <param name="cachedPassword">主窗口已经为这个包输过并验证过的口令；null 表示没有。</param>
    internal static async Task<Run> ExtractAsync(
        LangCatalog lang,
        AppSettings appSettings,
        ShellTask task,
        string archivePath,
        ProgressWindow progress,
        CancellationTokenSource cts,
        string? cachedPassword = null)
    {
        // 加密包先问一次口令再开工：解压到一半才报“要口令”会留下一堆半截文件。
        string? password = cachedPassword;
        var prompted = false;
        if (password is null && await Task.Run(() => ArchiveExtractor.IsPasswordProtected(archivePath)))
        {
            var prompt = new PasswordWindow(lang, initialPassword: null, requireConfirmation: false);
            if (await prompt.ShowDialog<bool>(progress) != true)
            {
                return new Run(null, null, false, false);
            }

            password = prompt.Password;
            prompted = true;
        }

        var destination = await ExtractDestinationAsync(
            archivePath, password, ExtractionDefaults.Load(appSettings).SingleFileToFolder);

        // 撞名先问一次、整批照这一个决定办（5.2.0），所以问话排在开工之前而不是解压中途。
        var overwrite = await AskAboutDuplicatesAsync(lang, progress, archivePath, destination, password);

        var settings = new ExtractionSettings(
            archivePath,
            destination,
            overwrite,
            Password: password,
            // 「解压并删除」与「解压」走的是同一套解压代码，差别只在这一个开关上。
            DeleteArchiveAfterExtraction: task == ShellTask.ExtractDelete,
            Progress: progress.CreateSink());

        try
        {
            await Task.Run(() => ArchiveExtractor.Extract(settings, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：静默收尾，不弹错误框吓唬人。
            return new Run(destination, password, prompted, false);
        }
        catch (Exception ex)
        {
            await ShowError(lang, progress, ex.Message);
            return new Run(destination, password, prompted, false);
        }

        return new Run(destination, password, prompted, true);
    }

    /// <summary>
    /// 一次解压的结果。<see cref="Succeeded"/> 为假时调用方别记口令、别开目录 ——
    /// 「解出来了才算这个口令是对的」，而失败或取消时打开一个装了一半的目录，
    /// 比不打开更容易让人误以为东西都在里面。
    /// </summary>
    internal readonly record struct Run(string? Destination, string? Password, bool Prompted, bool Succeeded);

    /// <summary>
    /// 解压落点：读一遍清单，看顶层是单文件、单子目录还是零散一堆。
    /// 只解析目录结构，不动数据，所以这一步在进度条上几乎不占时间。
    /// </summary>
    private static async Task<string> ExtractDestinationAsync(
        string archivePath, string? password, bool singleFileToFolder)
    {
        var listing = await Task.Run(() => ArchiveReader.Read(archivePath, password));

        return ExtractionLayout.Destination(
            archivePath, ExtractionLayout.Inspect(listing), singleFileToFolder);
    }

    /// <summary>
    /// 重名询问（FR-3.7）：<strong>整批一次问清</strong>。先把目标里已经存在的同名文件数一遍，
    /// 有冲突就弹一扇"这一次撞名的怎么算"，把答案翻成策略交给 Core —— 从此这一批不再逐个弹。
    /// <para>一个都没撞着就不弹，返回 <see cref="OverwritePolicy.Ask"/>：这一趟不该凭空多出一个框。
    /// 真到了落盘那一刻才发现撞了（别的程序刚建了一个同名文件）也不回头问 ——
    /// Ask 没有回调时 Core 按「重命名」落地，那是"不静默覆盖别人的东西"的缺省。</para>
    /// <para>数冲突要打开归档读清单，放后台线程；弹框必须在 UI 线程，所以两件事分在两侧，
    /// 中间不再需要 <c>Dispatcher.InvokeAsync(...).GetAwaiter().GetResult()</c> 那种
    /// "在工作线程里同步等一扇窗"的形状（它当年是把逐条询问接进解压循环里没办法的产物）。</para>
    /// </summary>
    internal static async Task<OverwritePolicy> AskAboutDuplicatesAsync(
        LangCatalog lang, Window owner, string archivePath, string destination, string? password)
    {
        var collisions = await Task.Run(() => ArchiveExtractor.FindCollisions(archivePath, destination, password));
        if (collisions.Count == 0)
        {
            return OverwritePolicy.Ask;
        }

        var dialog = new DuplicateFileWindow(lang, collisions);
        await dialog.ShowDialog<bool>(owner);
        return dialog.Decision switch
        {
            DuplicateDecision.Overwrite => OverwritePolicy.Overwrite,
            DuplicateDecision.Skip => OverwritePolicy.Skip,
            _ => OverwritePolicy.Rename,
        };
    }

    private static Task ShowError(LangCatalog lang, Window owner, string message)
        => new MessageWindow(lang, lang.Get("TEXT_TITLE_ERROR"), lang.Get("TEXT_ERR_EXTRACT_FAILED") + message)
            .ShowDialog(owner);
}
