using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Archive;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 把选中的格式登记到 Windows 的“打开方式”里（HKCU，不需要管理员权限）。
/// <para>
/// 只写 <c>OpenWithProgids</c>，<strong>不</strong>改扩展名键的默认值 —— 那等于抢下系统的默认
/// 程序，Windows 10/11 会弹“允许默认应用”的保护提示，而且覆盖掉用户已有的选择。目标是让
/// Andzip 出现在右键“打开方式”的列表里，选哪个仍由用户决定。
/// </para>
/// <para>
/// 撤销是干净的：每个格式一棵独立的 <c>Andzip.&lt;id&gt;</c> ProgID 子树，取消勾选时整棵删掉，
/// 再从对应扩展名的 <c>OpenWithProgids</c> 里删掉我们那个值，不碰别人的。
/// </para>
/// <para>
/// 全部读写都走 <c>HKCU\Software\Classes</c>：<c>HKEY_CLASSES_ROOT</c> 只是 HKCU 与 HKLM 的合并
/// 视图，普通用户能写的就是 HKCU 这一半。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FileAssociationService
{
    /// <summary>ProgID 前缀。设置存储与注册表都靠它识别“哪些是 Andzip 登记的”。</summary>
    public const string ProgIdPrefix = "Andzip.";

    private const string ClassesRoot = @"Software\Classes\";
    private const int ShellAssocChanged = 0x08000000;
    private const uint ShellFlushNow = 0x0002;

    /// <summary>登记一个格式。可重复调用（幂等）。</summary>
    public void Register(FileAssociation association, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var progId = ProgId(association);
        using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + progId))
        {
            key.SetValue(string.Empty, "Andzip " + association.Label);
            key.CreateSubKey("DefaultIcon").SetValue(string.Empty, executablePath + ",0");
            // %1 必须带引号：路径里有空格时不带引号会被拆成两个参数。
            key.CreateSubKey(@"shell\open\command").SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"");
        }

        foreach (var extension in association.Extensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey(
                ClassesRoot + "." + extension + @"\OpenWithProgids");
            openWith.SetValue(progId, string.Empty, RegistryValueKind.String);
        }
    }

    /// <summary>取消登记一个格式：删掉我们自己的 ProgID 子树与扩展名上挂的值。</summary>
    public void Unregister(FileAssociation association)
    {
        var progId = ProgId(association);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + progId, throwOnMissingSubKey: false);

        foreach (var extension in association.Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey(
                ClassesRoot + "." + extension + @"\OpenWithProgids", writable: true);
            if (openWith is not null && Array.IndexOf(openWith.GetValueNames(), progId) >= 0)
            {
                openWith.DeleteValue(progId, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>让资源管理器立刻刷新“打开方式”，否则要等 shell 重启才看得见。</summary>
    public void NotifyChanged() => SHChangeNotify(ShellAssocChanged, ShellFlushNow, IntPtr.Zero, IntPtr.Zero);

    public static string ProgId(FileAssociation association) => ProgIdPrefix + association.Id;

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
