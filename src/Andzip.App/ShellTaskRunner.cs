using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia.Controls.ApplicationLifetimes;

namespace Andzip.App;

/// <summary>
/// 后台任务（没有主窗口那一条路径）的分发处。
/// <para>
/// 注册表动词只负责「把这个路径交给 Andzip，做这件事」，做什么写在命令行的 <c>/task=</c> 里，
/// 于是解压和压缩共用同一个 exe、同一根进度条、同一套取消与退出逻辑。
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
        => task switch
        {
            ShellTask.Extract or ShellTask.ExtractHere =>
                DirectExtract.RunAsync(lang, settings, path, ShellTasks.ExtractDestination(path, task), desktop),
            _ => DirectCompress.RunAsync(lang, settings, task, path, desktop),
        };
}
