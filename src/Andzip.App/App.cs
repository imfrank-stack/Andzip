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

            // 「双击直接解压」：注册表 shell\extracterb 带着 /extract 拉起来。
            // 这条路径不建主窗口，所以生命周期要改成显式关闭，否则没有主窗口会让进程立刻退出。
            if (target is not null && HasSwitch(desktop.Args))
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
}