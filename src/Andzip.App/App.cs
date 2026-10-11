using Andzip.App.Services;
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
            var target = desktop.Args?.FirstOrDefault(IsExistingFile);

            if (target is not null)
            {
                RepairStaleRegistrations();
            }

            // 「双击直接解压」：要么命令行显式带 /extract，要么常规设置里把双击行为选成了直接解压。
            // 这条路径不建主窗口，所以生命周期要改成显式关闭，否则没有主窗口会让进程立刻退出。
            if (target is not null && SettingKeys.ShouldExtractDirectly(
                    settings.GetString(SettingKeys.DoubleClickAction), HasSwitch(desktop.Args)))
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = DirectExtract.RunAsync(lang, settings, target, desktop);
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

    private static bool IsExistingFile(string argument)
        => !argument.StartsWith('-') && File.Exists(argument);

    private static bool HasSwitch(IEnumerable<string>? arguments)
        => arguments?.Any(a => a.Equals(SettingKeys.ExtractSwitch, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// 把注册表里指向旧 exe 的命令行校正到当前这个文件。
    /// <para>
    /// 绿色版的文件名带版本号，升级之后注册表里那条命令还指着上一个 exe，而那个老二进制不认
    /// 新行为 —— 用户看到的就是「关联做了、设置也改了，双击还是老样子」。启动时顺手把自己
    /// 命名空间下的键校正过来，就不必每次升级都重做一遍关联。
    /// </para>
    /// <para>只在真的通过关联被拉起来时做（否则首次运行会平白写一次注册表），且只动 <c>Andzip.*</c>。</para>
    /// </summary>
    private static void RepairStaleRegistrations()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return;
        }

        try
        {
            var service = new FileAssociationService();
            if (service.RepairCommands(Environment.ProcessPath) > 0)
            {
                service.NotifyChanged();
            }
        }
        catch (Exception)
        {
            // 修不动就算了：命令行本身仍然可用，不该因为校正注册表失败就拒绝解压。
        }
    }
}
