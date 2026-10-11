using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Andzip.App;

/// <summary>
/// 没有主窗口的压缩执行体：右键「压缩为 ZIP / 7Z」。与解压一个道理 —— 只有百分比进度条，
/// 跑完自己消失，不插一张主窗口。
/// <para>
/// 一律用默认档位（正常压缩、无口令、不分卷）：右键是「随手压一下」的入口，弹对话框问设置
/// 就不再随手了。要口令、分卷、极限压缩，走主窗口的「新建压缩文件」。
/// </para>
/// <para>包名取源路径的同名兄弟位置，重名往后加序号 —— 右键是用户随手点的，静默覆盖已有的包不可原谅。</para>
/// </summary>
internal static class DirectCompress
{
    public static async Task RunAsync(
        LangCatalog lang,
        AppSettings settings,
        ShellTask task,
        string sourcePath,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(
            lang,
            cts,
            "TEXT_STATUS_COMPRESSING",
            cancelViaEsc: settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true),
            standalone: true);
        progress.Show();

        try
        {
            await CompressAsync(lang, task, sourcePath, progress, cts);
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

    private static async Task CompressAsync(
        LangCatalog lang,
        ShellTask task,
        string sourcePath,
        ProgressWindow progress,
        CancellationTokenSource cts)
    {
        var input = ArchiveInputItem.FromPath(sourcePath);
        var format = ArchiveFormatOption.FromId(FormatId(task));
        var output = ShellTasks.UniqueArchivePath(
            ShellTasks.ArchivePathFor(sourcePath, format.Extension, input.IsDirectory));

        var creation = new ArchiveCreationSettings(
            output,
            CompressionLevelOption.Default,
            Format: format,
            // 压文件夹时把文件夹本身装进去（docs.zip 里是 docs/…），右键压缩的语义就是"把这个压起来"。
            IncludeRootFolder: input.IsDirectory,
            // UI 线程构造，引擎工作线程上报的进度会自动投递回 UI 线程。
            Progress: new Progress<ArchiveProgress>(progress.Update));

        ArchiveCreationResult result;
        try
        {
            result = await Task.Run(() => ArchiveCreator.Create([input], creation, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：静默收尾，不留半成品（ArchiveOutputGuard 已经删掉了）。
            return;
        }

        // 引擎回传的"设置被忽略了"不能静默丢弃，但也不该盖在成功之上 —— 用户关心的是包在哪儿。
        if (result.Warnings is { Count: > 0 } warnings)
        {
            await new MessageWindow(lang, lang.Get("TEXT_TITLE_SETTINGS"), string.Join(Environment.NewLine, warnings))
                .ShowDialog(progress);
        }
    }

    private static string FormatId(ShellTask task) => task switch
    {
        ShellTask.CompressSevenZip => "7Z",
        ShellTask.CompressZip => "ZIP",
        _ => throw new ArgumentOutOfRangeException(nameof(task), task, "只有压缩任务会走到这里。"),
    };

    private static Task ShowError(LangCatalog lang, Window owner, string message)
        => new MessageWindow(lang, lang.Get("TEXT_TITLE_ERROR"), lang.Get("TEXT_ERR_COMPRESS_FAILED") + message)
            .ShowDialog(owner);
}
