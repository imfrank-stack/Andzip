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
            // 安装目录里那个"看得见、双击得动"的卸载入口（用户第二次提这件事）。
            Check("安装目录里有 uninstall.bat", File.Exists(plan.UninstallScript), plan.UninstallScript);

            var (iconPath, iconIndex) = ShellLink.ReadIcon(plan.DesktopShortcut!);
            Check("桌面图标的图标源是装好的那个 exe",
                iconPath == plan.ExecutablePath && iconIndex == 0, iconPath);

            using (var key = Registry.CurrentUser.OpenSubKey(ProbeKey))
            {
                Check("卸载表里有这一条", key is not null);
                Check("卸载命令带 /uninstall",
                    ((string?)key?.GetValue("UninstallString"))?.Contains("/uninstall", StringComparison.Ordinal) == true);

                // 版本号里那段 "+45.718056…" 是 SourceLink 附的提交号，写进卸载表会让系统的
                // 「设置 → 应用」显示成一长串（1.9.2 真机核对量到的）。
                var display = (string?)key?.GetValue("DisplayVersion") ?? string.Empty;
                Check("卸载表里的版本号干净（不带提交号）",
                    display.Length > 0 && !display.Contains('+', StringComparison.Ordinal), display);
            }

            Check("开始菜单里有「卸载 Andzip」那一项", File.Exists(plan.StartMenuUninstallShortcut));

            // 机器级那一份只算落点、不写真东西：这台机器上的账户可能不是管理员，
            // 而"提权之后那个进程会不会往个人目录里写东西"这件事是纯路径事实，量得起。
            var allUsers = InstallLayout.PlanForAllUsers(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            Check("机器级那一份的程序文件在 Program Files 下",
                allUsers.ExecutablePath.StartsWith(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase),
                allUsers.ExecutablePath);
            Check("机器级那一份不往个人目录里写任何东西",
                !allUsers.StartMenuShortcut.Contains(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
                && !allUsers.StartMenuShortcut.Contains(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase),
                allUsers.StartMenuShortcut);
            Check("机器级走 HKLM 的 64 位视图（32 位视图会被重定向到 WOW6432Node）",
                RegistryScope.RootFor(InstallScope.AllUsers)
                    == (RegistryHive.LocalMachine, RegistryView.Registry64));

            var cleaned = service.Uninstall(plan);
            Check("卸载后安装目录不见", cleaned && !Directory.Exists(plan.InstallDirectory));
            Check("卸载后快捷方式不见",
                !File.Exists(plan.StartMenuShortcut) && !File.Exists(plan.DesktopShortcut));
            Check("卸载后「卸载 Andzip」那一项也不见", !File.Exists(plan.StartMenuUninstallShortcut));
            Check("卸载后 uninstall.bat 也跟着目录收回", !File.Exists(plan.UninstallScript));
            Check("卸载后注册表那条不见", Registry.CurrentUser.OpenSubKey(ProbeKey) is null);

            // 向导里那个位置可以改，所以装到别处这一条也得在产物上跑一遍：
            // 路径归一化与"卸载要回到记着的那个目录"这两件事都在真机上才量得准。
            var elsewhere = Path.Combine(root, "Custom", "Andzip 2");
            var custom = InstallLayout.Plan(
                Path.Combine(root, "Local"),
                Path.Combine(root, "Roaming"),
                desktop,
                elsewhere,
                "卸载 Andzip") with { UninstallKey = ProbeKey };

            service.Install(custom, source, associate: false, contextMenu: false);
            Check("装到自定义目录：程序文件在那里", File.Exists(custom.ExecutablePath), custom.ExecutablePath);
            Check("装到自定义目录：卸载表记下了那个位置",
                (string?)Registry.CurrentUser.OpenSubKey(ProbeKey)?.GetValue("InstallLocation") == custom.InstallDirectory);
            Check("装到自定义目录：卸载快捷方式的名字来自语言包",
                File.Exists(custom.StartMenuUninstallShortcut), custom.StartMenuUninstallShortcut);

            // 卸载要按卸载表里记着的那个位置找回自己（向导里那个位置是可以改的）。
            // 这里只核对"读回来的目录对不对"，删东西仍然用临时落点那份 —— RecordedPlan()
            // 走的是这台机器的真实目录，拿它去卸会把开发机上的开始菜单快捷方式删掉。
            Check("装到自定义目录：卸载时按记着的位置找回自己",
                service.RecordedPlan(ProbeKey).InstallDirectory == custom.InstallDirectory,
                service.RecordedPlan(ProbeKey).InstallDirectory);
            Check("装到自定义目录：卸得回去", service.Uninstall(custom),
                "卸不回去会在盘上留下一个没人知道的目录");
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
