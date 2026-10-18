using System.Diagnostics;
using System.Runtime.Versioning;
using Andzip.Core.Installation;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 作用域 → 注册表根。两个作用域下的<strong>相对键路径是同一个</strong>
/// （<c>Software\Classes\…</c>、<c>Software\Microsoft\Windows\CurrentVersion\Uninstall\Andzip</c>），
/// 所以各处只要换根，不必再维护两套键名。
/// <para><strong>机器级必须显式要 64 位视图</strong>：32 位进程眼里 <c>HKLM\Software\Classes</c> 会被
/// 重定向到 <c>WOW6432Node</c>，于是"注册表里明明写了、64 位的资源管理器里看不见"。
/// 本项目出的是 x64 产物，看着撞不上，但这条不该建立在"我们正好是 64 位"这种隐含前提上。</para>
/// <para>HKCU 那一侧没有 32/64 之分（不存在 WOW6432Node），保持 <see cref="RegistryView.Default"/>。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryScope
{
    public static RegistryKey Open(InstallScope scope)
    {
        var (hive, view) = RootFor(scope);
        return RegistryKey.OpenBaseKey(hive, view);
    }

    /// <summary>单独把这一条映射抽出来，是为了能被一条用例钉住（真写 <c>HKLM</c> 要管理员，测不起）。</summary>
    public static (RegistryHive Hive, RegistryView View) RootFor(InstallScope scope) => scope switch
    {
        InstallScope.AllUsers => (RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => (RegistryHive.CurrentUser, RegistryView.Default),
    };
}

/// <summary>
/// 要管理员权限时怎么办：把<strong>自己</strong>以管理员身份再启一次，这一份随后退出。
/// <para>不去问"这个账户是不是管理员"（<c>WindowsIdentity</c> 不随非 Windows 目标的共享框架提供，
/// 而这个项目还要在 Linux 上构建）。判据改成实测：<see cref="Needed"/> 问的是
/// "这两处我现在写不写得进去" —— 安装目录与 <c>HKLM</c> 卸载表，那正是提权与否的分界，
/// 而且与安装/卸载真正会失败的条件是同一件事。用两个不同的问题去判断同一件事，
/// 迟早会不一致（5.8.3 之前只问目录，就在"Program Files 对普通用户可写"的机器上漏掉了 <c>HKLM</c>）。</para>
/// <para>用户在 UAC 上按「否」（或者这台机器上这个账户根本不是管理员，弹的是要别人口令的框）时
/// <c>Process.Start</c> 抛异常：这里只回 false，由界面照实说"没拿到管理员权限，装不了所有用户"。
/// <strong>不悄悄退回只给自己装</strong> —— 那是用户没同意过的动作。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Elevation
{
    /// <summary>
    /// 这一次是不是非得提权不可（当前用户作用域永远不需要）。
    /// <para><strong>两处都要问</strong>：机器级那一套写的是<em>两个</em>地方 —— 安装目录与
    /// <c>HKLM</c>。只看目录会在"D 盘的 Program Files 恰好对普通用户可写"的机器上给出
    /// 错误的答案（这台机器实测就是：<c>D:\Program Files</c> 给 Authenticated Users 是 M，
    /// 而 <c>HKLM\Software\…</c> 仍然拒绝访问）—— 于是卸载不问提权，走到删卸载表那一步
    /// 才抛"不允许所请求的注册表访问权"，用户看到的就是"卸载失败"。
    /// </para>
    /// <para>注册表这一侧<strong>只开不写</strong>：<c>OpenSubKey(…, writable: true)</c>
    /// 本身就是权限探测，没有管理员权限会当场抛 <see cref="UnauthorizedAccessException"/>，
    /// 不会留下探针键。</para>
    /// </summary>
    public static bool Needed(InstallPlan plan, Func<InstallScope, RegistryKey>? registryRoot = null)
        => plan.Scope == InstallScope.AllUsers
           && (!InstallTarget.IsWritable(plan.InstallDirectory) || !MachineRegistryWritable(plan, registryRoot));

    /// <summary>
    /// 当前进程能不能写机器级注册表（卸载表所在的那一处）。非管理员返回 false。
    /// <para>探的是<strong>卸载表键的父层</strong>，不是写死的路径：生产环境里它是
    /// <c>HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall</c>，而测试与
    /// <c>/selftest</c> 会注入一个 <c>HKCU</c> 探针根 —— 提权判断必须跟着同一个根走，
    /// 否则"临时目录 + 探针根"的整条机器级往返会被真的 <c>HKLM</c> 权限挡下来。</para>
    /// </summary>
    private static bool MachineRegistryWritable(
        InstallPlan plan,
        Func<InstallScope, RegistryKey>? registryRoot)
    {
        var parent = Path.GetDirectoryName(plan.UninstallKey);

        // 注入的探针根本身就是那一层（测试里的卸载键只有一段）。它是用写权限建出来的，
        // 能拿到它就已经说明写得动；生产的卸载键永远有父层，走不到这里。
        if (string.IsNullOrEmpty(parent))
        {
            return true;
        }

        try
        {
            using var root = (registryRoot ?? RegistryScope.Open)(plan.Scope);
            using var key = root.OpenSubKey(parent, writable: true);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>以管理员身份重启自己并带上这些参数。成功返回 true —— 调用方随后就该收窗退出，
    /// 让那一个新的、提了权的实例接着做。</summary>
    public static bool TryRelaunch(string arguments)
    {
        if (Environment.ProcessPath is not { Length: > 0 } own)
        {
            return false;
        }

        try
        {
            // runas 这个动词只有 ShellExecute 认，所以这里必须 UseShellExecute = true
            Process.Start(new ProcessStartInfo
            {
                FileName = own,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });

            return true;
        }
        catch (Exception)
        {
            // 拒绝提权、账户没有管理员、路径有问题 —— 一律"没提上来"，由界面说实话
            return false;
        }
    }
}
