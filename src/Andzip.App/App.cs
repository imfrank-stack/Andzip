using Andzip.App.Services;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;

namespace Andzip.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 语言包：程序目录 langs/ 优先（便携覆盖），否则用内嵌资源（单文件绿色版）。
            var lang = LangLoader.CreateCatalog(typeof(App).Assembly, AppContext.BaseDirectory);
            var settings = AppSettings.Load(AppSettings.DefaultFilePath);
            var target = desktop.Args?.FirstOrDefault(IsExistingPath);

            if (target is not null)
            {
                RepairStaleShellEntries(lang, settings);
            }

            // 不建主窗口的两条路径：右键动词带来的 /task=，以及双击 + 「常规 → 双击 = 直接解压」。
            // 生命周期要改成显式关闭，否则没有主窗口会让进程立刻退出。
            if (target is not null && TryResolveTask(desktop.Args, settings, out var task))
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = ShellTaskRunner.RunAsync(lang, settings, task, target, desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var window = new MainWindow(lang, settings);
            desktop.MainWindow = window;

            // 命令行带压缩包路径时直接打开：文件关联与「发送到 Andzip」都依赖这条入口。
            if (target is not null)
            {
                _ = window.OpenArchive(target);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 命令行里的目标路径。目录也算 —— 右键「压缩到」既给文件也给文件夹，
    /// 而 <c>File.Exists</c> 对文件夹返回 false，会把这个入口整个漏掉。
    /// </summary>
    private static bool IsExistingPath(string argument)
        => !argument.StartsWith('-') && (File.Exists(argument) || Directory.Exists(argument));

    /// <summary>
    /// 这次启动要不要走「没有主窗口」的后台路径，以及做哪件事。
    /// <para>
    /// 右键动词在命令行里写明了 <c>/task=</c>，直接照办；双击没有开关，做什么由
    /// 「常规 → 双击打开压缩文件时」决定 —— 行为放在设置里而不是注册表的默认动词里，
    /// 改设置才不用重写所有 ProgID。
    /// </para>
    /// <para><c>open</c> 是反例：它要的正是主窗口，交回给调用方去建。</para>
    /// </summary>
    private static bool TryResolveTask(IEnumerable<string>? arguments, AppSettings settings, out ShellTask task)
    {
        if (ShellTasks.TryParse(arguments, out task))
        {
            return task != ShellTask.Open;
        }

        if (SettingKeys.ShouldExtractDirectly(settings.GetString(SettingKeys.DoubleClickAction)))
        {
            task = ShellTask.Extract;
            return true;
        }

        task = default;
        return false;
    }

    /// <summary>
    /// 把注册表里指向旧 exe 的命令行、以及缺条目的右键动词校正到当前这个版本。
    /// <para>
    /// 绿色版的文件名带版本号，升级之后注册表里那条命令还指着上一个 exe，而那个老二进制不认
    /// 新行为 —— 用户看到的就是「关联做了、设置也改了，双击还是老样子」。启动时顺手把自己
    /// 命名空间下的键校正过来，就不必每次升级都重做一遍关联。
    /// </para>
    /// <para>只在真的通过关联被拉起来时做（否则首次运行会平白写一次注册表），且只动 <c>Andzip.*</c>。</para>
    /// <para>解压动词按「设置 → 上下文菜单」的选择补写：升级后新认出去的格式也该有右键菜单，
    /// 但用户明确关掉过的那一项不能被自愈偷偷加回来。压缩动词只补齐<strong>已经装了</strong>的范围，
    /// 没勾过的用户不会某天突然发现菜单里多了三项。</para>
    /// </summary>
    private static void RepairStaleShellEntries(LangCatalog lang, AppSettings settings)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return;
        }

        var executable = Environment.ProcessPath;

        try
        {
            var showExtractVerbs = settings.GetBool(SettingKeys.ContextMenuExtractVerbs, fallback: true);
            var service = new FileAssociationService();
            var labels = ShellVerbLabels.Extraction(lang, showExtractVerbs);
            if (service.RepairCommands(executable, labels) > 0)
            {
                service.NotifyChanged();
            }
        }
        catch (Exception)
        {
            // 修不动就算了：命令行本身仍然可用，不该因为校正注册表失败就拒绝解压。
        }

        try
        {
            var contextMenu = new ContextMenuService();
            if (contextMenu.RepairVerbs(executable, ShellVerbLabels.Compression(lang)) > 0)
            {
                contextMenu.NotifyChanged();
            }
        }
        catch (Exception)
        {
            // 同上：右键压缩不是用户这一步要的结果，缺条目下次再补。
        }
    }
}
