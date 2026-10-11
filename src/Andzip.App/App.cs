using Andzip.Core.Lang;
using Avalonia;
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
            var window = new MainWindow(lang);
            desktop.MainWindow = window;

            // 命令行带压缩包路径时直接打开：文件关联与「发送到 Andzip」都依赖这条入口。
            var target = desktop.Args?.FirstOrDefault(IsExistingFile);
            if (target is not null)
            {
                _ = window.OpenArchive(target);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool IsExistingFile(string argument)
        => !argument.StartsWith('-') && File.Exists(argument);
}