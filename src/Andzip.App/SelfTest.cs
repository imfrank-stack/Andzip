using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Microsoft.Win32;

namespace Andzip.App;

/// <summary>
/// 对<strong>正在运行的这个 exe</strong>做一次安装往返，用来当打包门禁。
/// <para>为什么需要它：单元测试跑的是未裁剪的 <c>Andzip.dll</c>，而发布出来的是
/// 单文件 + 裁剪 + 压缩过的 <c>Andzip.exe</c>。这两者的差别不是理论上的 ——
/// 1.8.3 那次安装器报 "Built-in COM has been disabled via a feature switch"，
/// 建不出桌面快捷方式，而<strong>全部 1101 条断言都是绿的</strong>：
/// 快捷方式走 COM，裁剪把 built-in COM 整个去掉了，测试进程里却还在。</para>
/// <para>所以这条检查必须跑在产物上。它不碰真配置：落点是临时目录，注册表写的是探针键，
/// 文件关联与右键菜单一律传 false。</para>
/// <para>用法：<c>Andzip.exe /selftest</c>，退出码 0 = 通过。每行一条 <c>ok</c> / <c>FAIL</c>。</para>
/// </summary>
internal static class SelfTest
{
    private const string ProbeKey = @"Software\Andzip.Tests\SelfTest";

    public static bool IsRequested(string[] args)
        => args.Any(a => string.Equals(a, "/selftest", StringComparison.OrdinalIgnoreCase));

    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        var root = Directory.CreateTempSubdirectory("andzip-selftest-").FullName;
        var failures = 0;

        void Check(string what, bool passed, string? detail = null)
        {
            Console.WriteLine($"{(passed ? "  ok  " : "FAIL  ")}{what}{(detail is null ? "" : $" ({detail})")}");
            if (!passed)
            {
                failures++;
            }
        }

        try
        {
            var desktop = Path.Combine(root, "Desktop");
            Directory.CreateDirectory(desktop);

            var plan = InstallLayout.Plan(
                Path.Combine(root, "Local"),
                Path.Combine(root, "Roaming"),
                desktop) with { UninstallKey = ProbeKey };

            var source = Environment.ProcessPath;
            Check("拿得到自己的 exe 路径", source is not null && File.Exists(source), source);
            if (source is null)
            {
                return 1;
            }

            var lang = LangLoader.CreateCatalog(typeof(SelfTest).Assembly, RepositoryRoot(), "SimpChinese");
            var service = new InstallationService(lang);

            // 只读的一条：桌面目录解析。上面那条往返测试自己传临时目录，所以"这台机器上
            // 到底取不取得到桌面"这件事它量不到 —— 而 FOLDERID 写错时表现正是"桌面上没有图标，
            // 界面上却写着已创建"（1.8.4 真机核对抓到的）。
            var shellDesktop = InstallationService.DesktopFolder();
            Check("桌面目录解析得到（走这台机器的 shell）",
                shellDesktop is not null && Directory.Exists(shellDesktop), shellDesktop);

            var outcome = service.Install(plan, source, associate: false, contextMenu: false);
            Check("程序文件落到安装目录", File.Exists(plan.ExecutablePath));
            Check("复制过来的字节与源一致",
                new FileInfo(plan.ExecutablePath).Length == new FileInfo(source).Length,
                $"{new FileInfo(plan.ExecutablePath).Length} vs {new FileInfo(source).Length}");
            Check("开始菜单快捷方式建出来了（这一步走 COM）", File.Exists(plan.StartMenuShortcut));
            Check("桌面快捷方式建出来了", File.Exists(plan.DesktopShortcut));

            var (iconPath, iconIndex) = ShellLink.ReadIcon(plan.DesktopShortcut!);
            Check("桌面图标的图标源是装好的那个 exe",
                iconPath == plan.ExecutablePath && iconIndex == 0, iconPath);

            using (var key = Registry.CurrentUser.OpenSubKey(ProbeKey))
            {
                Check("卸载表里有这一条", key is not null);
                Check("卸载命令带 /uninstall",
                    ((string?)key?.GetValue("UninstallString"))?.Contains("/uninstall", StringComparison.Ordinal) == true);
            }

            var cleaned = service.Uninstall(plan);
            Check("卸载后安装目录不见", cleaned && !Directory.Exists(plan.InstallDirectory));
            Check("卸载后快捷方式不见",
                !File.Exists(plan.StartMenuShortcut) && !File.Exists(plan.DesktopShortcut));
            Check("卸载后注册表那条不见", Registry.CurrentUser.OpenSubKey(ProbeKey) is null);
        }
        catch (Exception ex)
        {
            // 这条门禁跑在产物上，看不到调试器 —— 栈要原样打出来才有法定位
            Check("自检过程没有抛异常", false, ex.ToString().ReplaceLineEndings(" | "));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProbeKey, throwOnMissingSubKey: false);

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>语言包要的是<strong>含有</strong> langs/ 的那一层；单文件版从内嵌资源读，这里只是兜底。</summary>
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "langs")))
            {
                return dir.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }
}
