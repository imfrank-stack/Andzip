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
/// "这个位置我现在写不写得进去" —— 那正是提权与否的分界，而且与安装真正会失败的条件是同一件事
/// （<see cref="InstallTarget.IsWritable"/>）。用两个不同的问题去判断同一件事，迟早会不一致。</para>
/// <para>用户在 UAC 上按「否」（或者这台机器上这个账户根本不是管理员，弹的是要别人口令的框）时
/// <c>Process.Start</c> 抛异常：这里只回 false，由界面照实说"没拿到管理员权限，装不了所有用户"。
/// <strong>不悄悄退回只给自己装</strong> —— 那是用户没同意过的动作。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Elevation
{
    /// <summary>这一次是不是非得提权不可（当前用户作用域永远不需要）。</summary>
    public static bool Needed(InstallPlan plan)
        => plan.Scope == InstallScope.AllUsers && !InstallTarget.IsWritable(plan.InstallDirectory);

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
