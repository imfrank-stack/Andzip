using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Andzip.App;

/// <summary>
/// 后台任务（没有主窗口那一条路径）的分发处。
/// <para>
/// 注册表动词只负责「把这个路径交给 Andzip，做这件事」，做什么写在命令行的 <c>/task=</c> 里，
/// 于是解压、直接压缩、以及"把新建压缩文件对话框开起来"共用同一个 exe 与同一套退出逻辑。
/// </para>
/// </summary>
internal static class ShellTaskRunner
{
    public static Task RunAsync(
        LangCatalog lang,
        AppSettings settings,
        ShellTask task,
        string path,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (ShellTasks.IsExtraction(task))
        {
            return DirectExtract.RunAsync(lang, settings, task, path, desktop);
        }

        return task == ShellTask.CompressDialog
            ? ShowNewArchive(lang, settings, path, desktop)
            : DirectCompress.RunAsync(lang, settings, task, path, desktop);
    }

    /// <summary>
    /// 右键「压缩到…」：把「新建压缩文件」对话框开起来，输入列表里已经放好选中的那个路径。
    /// <para>
    /// 这条路径上用户可能取消、也可能压完再改主意，所以<strong>窗口关掉</strong>才结束进程 ——
    /// 不像另外两条压缩那样"跑完就走"。没有主窗口，所以要 <c>Show</c> 而不是 <c>ShowDialog</c>，
    /// 并且得出现在任务栏上，否则 alt-tab 找不到它。
    /// </para>
    /// </summary>
    private static Task ShowNewArchive(
        LangCatalog lang,
        AppSettings settings,
        string path,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = new NewArchiveWindow(
            lang,
            CompressionDefaults.Load(settings),
            seedPaths: [path],
            lastOutputFolder: settings.GetString(SettingKeys.CompressLastOutputFolder))
        {
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Closed += (_, _) => desktop.Shutdown();
        window.Show();
        return Task.CompletedTask;
    }
}
