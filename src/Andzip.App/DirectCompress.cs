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
/// <para>
/// 档位与排除规则读「设置 → 压缩」，但<strong>不</strong>读其中的「压缩完成后不关闭进度窗口」：
/// 右键这条路径的既定行为就是跑完即消失（用户明确要求过），那条设置是给「新建压缩文件」对话框
/// 里想留下来看一眼体积比的人准备的。两者不是一回事，别在这里合并。
/// </para>
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
        var defaults = CompressionDefaults.Load(settings);
        var progress = new ProgressWindow(
            lang,
            cts,
            "TEXT_STATUS_COMPRESSING",
            cancelViaEsc: settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true),
            standalone: true);
        progress.Show();

        try
        {
            await CompressAsync(lang, task, sourcePath, defaults, progress, cts);
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
        CompressionDefaults defaults,
        ProgressWindow progress,
        CancellationTokenSource cts)
    {
        var input = ArchiveInputItem.FromPath(sourcePath);
        var format = ArchiveFormatOption.FromId(FormatId(task));
        var output = ShellTasks.UniqueArchivePath(
            ShellTasks.ArchivePathFor(sourcePath, format.Extension, input.IsDirectory));

        // 档位与排除规则来自「设置 → 压缩」：右键压缩是"随手压一下"，但"随手"的口味应该由用户定 ——
        // 他把默认级别调到极限，右键出来的就该是极限。
        var creation = new ArchiveCreationSettings(
            output,
            defaults.Level,
            Format: format,
            ExcludeThumbsDb: defaults.ExcludeThumbsDb,
            ExcludePatterns: defaults.ParsedExcludePatterns,
            // 压文件夹时把文件夹本身装进去（docs.zip 里是 docs/…），右键压缩的语义就是"把这个压起来"。
            IncludeRootFolder: input.IsDirectory && defaults.IncludeRootFolder,
            TestAfterCreation: defaults.TestAfterCreation,
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
