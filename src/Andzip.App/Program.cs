using Avalonia;

namespace Andzip.App;

internal static class Program
{
    /// <summary>未处理异常日志（单文件绿色版无控制台，崩溃信息写入该文件便于诊断）。</summary>
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "andzip-crash.log");

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception);
            e.SetObserved();
        };

        // 打包门禁：在产物自己身上跑一次安装往返，不起界面。
        // 单元测试跑的是未裁剪的 dll，有些故障（built-in COM 被裁掉就是其一）只在产物里现形。
        if (SelfTest.IsRequested(args))
        {
            Environment.Exit(OperatingSystem.IsWindows() ? SelfTest.Run(args) : 1);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            File.WriteAllText(CrashLogPath, $"{DateTime.Now:O}{Environment.NewLine}{exception}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}