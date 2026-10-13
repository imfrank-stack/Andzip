using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Archive;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>一次安装/卸载实际做了什么，用来在界面上如实报告（不写"成功"如果只做成一半）。</summary>
internal sealed record InstallOutcome(bool Changed, string ExecutablePath, IReadOnlyList<string> BlockedExtensions);

/// <summary>
/// 「正常安装」这条路径：把绿色版那一个 exe 变成桌面上有图标、开始菜单里有项、
/// 控制面板里能卸载的程序。
/// <para><strong>装的是同一个单文件 exe</strong>，不是解出一个目录。绿色版本来就是一个文件，
/// 复制一份进 <c>%LOCALAPPDATA%\Programs\Andzip</c> 就够了 —— 再做一个"多文件安装版"
/// 只会让出包脚本要维护两套产物、两套体积数字。</para>
/// <para><strong>全程只写当前用户</strong>：程序文件在 LOCALAPPDATA、快捷方式在 APPDATA、
/// 卸载表在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall</c>。
/// 一处都不需要管理员，也不需要 UAC 弹窗。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallationService
{
    private const string Publisher = "imfrank-stack";

    private readonly LangCatalog _lang;

    public InstallationService(LangCatalog lang) => _lang = lang;

    /// <summary>按这台机器的真实目录算出该装到哪（桌面被 OneDrive 改指向时也跟着走）。</summary>
    public InstallPlan CurrentPlan() => InstallLayout.Plan(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        DesktopFolder());

    /// <summary>
    /// 装（或重装）。
    /// </summary>
    /// <param name="plan">落点。测试传的是临时目录 + 探针键名，真机传 <see cref="CurrentPlan"/>。</param>
    /// <param name="sourceExe">要被复制过去的那个 exe（通常是正在运行的安装器自己）。</param>
    public InstallOutcome Install(InstallPlan plan, string sourceExe, bool associate, bool contextMenu)
    {
        Directory.CreateDirectory(plan.InstallDirectory);

        if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(plan.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceExe, plan.ExecutablePath, overwrite: true);
        }

        var description = _lang.Label("TEXT_INSTALLER_SHORTCUT_HINT");
        ShellLink.Create(plan.StartMenuShortcut, plan.ExecutablePath, null, description);
        if (plan.DesktopShortcut is { } desktop)
        {
            ShellLink.Create(desktop, plan.ExecutablePath, null, description);
        }

        WriteUninstallEntry(plan, associate, contextMenu);

        var blocked = new List<string>();
        if (associate)
        {
            var associations = new FileAssociationService();
            var labels = ShellVerbLabels.Extraction(_lang);
            foreach (var id in FileAssociationCatalog.InstallerDefaults)
            {
                if (FileAssociationCatalog.Find(id) is not { } association)
                {
                    continue;
                }

                blocked.AddRange(associations.Register(association, plan.ExecutablePath, labels).BlockedExtensions);
            }

            associations.NotifyChanged();
        }

        if (contextMenu)
        {
            var menu = new ContextMenuService();
            menu.Install(ShellVerbLabels.Compression(_lang), plan.ExecutablePath, forFiles: true, forFolders: true);
            menu.NotifyChanged();
        }

        return new InstallOutcome(true, plan.ExecutablePath, blocked);
    }

    /// <summary>
    /// 卸载：把 <see cref="Install"/> 做过的一件件收回来，最后删程序文件。
    /// <para><strong>关联与右键菜单按安装时记下的那两条决定</strong>：没记着当初勾过，
    /// 就不去动用户后来自己在「设置」里登记的东西 —— 卸载程序不该顺手删掉它没做过的事。
    /// 这也让这一条可以在测试里真跑一遍而不碰开发机上的配置。</para>
    /// </summary>
    public bool Uninstall(InstallPlan plan)
    {
        var (associated, contextMenu) = ReadUninstallFlags(plan);

        ShellLink.Delete(plan.StartMenuShortcut);
        if (plan.DesktopShortcut is { } desktop)
        {
            ShellLink.Delete(desktop);
        }

        Registry.CurrentUser.DeleteSubKeyTree(plan.UninstallKey, throwOnMissingSubKey: false);

        var associations = new FileAssociationService();
        if (associated)
        {
            foreach (var id in FileAssociationCatalog.InstallerDefaults)
            {
                if (FileAssociationCatalog.Find(id) is { } association && associations.IsRegistered(association))
                {
                    associations.Unregister(association, []);
                }
            }

            associations.NotifyChanged();
        }

        var menu = new ContextMenuService();
        if (contextMenu)
        {
            menu.Remove();
            menu.NotifyChanged();
        }

        return TryRemoveProgramFiles(plan);
    }

    /// <summary>
    /// 删掉安装目录里的程序文件。
    /// <para>从「控制面板 → 卸载」进来时，我们<strong>就是</strong>那个正在运行的 exe，
    /// 系统不允许删一个镜像还被映射着的文件。这里的处理是：让一个脱离本进程的
    /// <c>cmd</c> 等一两秒再删 —— 那之前我们已经退出，文件就不再被占用了。
    /// 界面会把这一句如实说出来，不假装当场删干净了。</para>
    /// </summary>
    private bool TryRemoveProgramFiles(InstallPlan plan)
    {
        try
        {
            if (Directory.Exists(plan.InstallDirectory))
            {
                Directory.Delete(plan.InstallDirectory, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            ScheduleDeleteOnExit(plan.InstallDirectory);
            return false;
        }
    }

    private static void ScheduleDeleteOnExit(string directory)
    {
        // 路径来自 Environment.ProcessPath，不是用户输入；仍然整段加引号，
        // 因为 %LOCALAPPDATA% 里出现空格是常事。
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{directory}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(start);
        }
        catch (InvalidOperationException)
        {
            // 起不了辅助进程只是"目录留在那儿"，不该把已经完成的卸载报成失败
        }
    }

    private void WriteUninstallEntry(InstallPlan plan, bool associate, bool contextMenu)
    {
        using var key = Registry.CurrentUser.CreateSubKey(plan.UninstallKey);

        var version = FileVersionInfo.GetVersionInfo(plan.ExecutablePath).ProductVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";

        key.SetValue("DisplayName", "Andzip");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", plan.InstallDirectory);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

        // DisplayIcon 指回 exe 本身：控制面板那一列小图标因此与桌面上那个是同一张
        key.SetValue("DisplayIcon", plan.ExecutablePath);
        key.SetValue("UninstallString", $"\"{plan.ExecutablePath}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // 当初做过哪两件事，卸载时才好照着收回来
        key.SetValue(ValueAssociate, associate ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(ValueContextMenu, contextMenu ? 1 : 0, RegistryValueKind.DWord);

        var sizeKb = (long)new FileInfo(plan.ExecutablePath).Length / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
    }

    private const string ValueAssociate = "AndzipAssociate";
    private const string ValueContextMenu = "AndzipContextMenu";

    private static (bool Associate, bool ContextMenu) ReadUninstallFlags(InstallPlan plan)
    {
        using var key = Registry.CurrentUser.OpenSubKey(plan.UninstallKey);
        return (AsBool(key?.GetValue(ValueAssociate)), AsBool(key?.GetValue(ValueContextMenu)));

        static bool AsBool(object? value) => value is int number && number != 0;
    }

    // ------------------------------------------------------ 桌面目录

    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB25-4554-B0CE-9B755845BEE4");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    /// <summary>
    /// 真实的桌面目录。<c>Environment.SpecialFolder.Desktop</c> 在 OneDrive 接管过的机器上
    /// 会指到没在桌面上显示的那个旧路径，所以走已知文件夹接口。
    /// </summary>
    public static string? DesktopFolder()
    {
        var id = FolderIdDesktop;
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var memory) != 0 || memory == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(memory);
        }
        finally
        {
            CoTaskMemFree(memory);
        }
    }
}
