using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 资源管理器右键菜单里的「压缩为 …」项，用静态注册表动词实现（不需要管理员，也不需要 COM shell 扩展）。
/// <para>
/// 文件写在 <c>HKCU\Software\Classes\*\shell</c> 下，文件夹写在 <c>…\Directory\shell</c> 下，
/// 两个范围各一个开关。键名一律带 <see cref="VerbPrefix"/> 前缀，因此撤销就是把这几个子树删掉，
/// 不碰别的软件加的动词。
/// </para>
/// <para>
/// <strong>Windows 11 的限制</strong>：静态动词会落在右键菜单的「显示更多选项」二级菜单里，
/// 不是 Win11 新菜单的第一层。要进第一层必须实现 <c>IExplorerCommand</c> 的 in-proc shell 扩展，
/// 那是另一个量级的东西（额外的原生 DLL、注册、崩溃会把资源管理器一起带崩），本版不做。
/// </para>
/// <para>菜单文字是静态的：注册表动词没法把 <c>%1</c> 的实际名字渲染进标题，所以写的是
/// 「压缩为 ZIP」而不是参考软件那种「压缩为 "文件名.zip"」。</para>
/// <para>命令带 <c>%1</c> 而不是 <c>%V</c>：静态动词拿不到多选的可信列表，因此一次只压一个文件或一个文件夹。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ContextMenuService
{
    /// <summary>我们自己动词的键名前缀，用于识别与清理。</summary>
    public const string VerbPrefix = "Andzip.";

    /// <summary>「所有文件」范围。</summary>
    public const string FilesScope = "*";

    /// <summary>「文件夹」范围。</summary>
    public const string FoldersScope = "Directory";

    private const string ClassesRoot = @"Software\Classes\";

    /// <summary>一个要装的压缩动词：注册表键名后缀 + 命令行开关 + 菜单文字。</summary>
    public sealed record CompressionVerb(string Key, string Switch, string Label);

    /// <summary>
    /// 按两个开关写压缩动词。可重复调用（幂等）：每次都先把我们的键清空再写，
    /// 这样在设置里取消勾选的那几项不会残留在菜单上。
    /// </summary>
    public void Install(IReadOnlyList<CompressionVerb> verbs, string executablePath, bool forFiles, bool forFolders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        Remove();

        foreach (var (scope, enabled) in Scopes(forFiles, forFolders))
        {
            if (!enabled)
            {
                continue;
            }

            using var shell = Registry.CurrentUser.CreateSubKey(ClassesRoot + scope + @"\shell");
            foreach (var verb in verbs)
            {
                using var key = shell.CreateSubKey(VerbPrefix + verb.Key);
                key.SetValue(string.Empty, verb.Label);
                key.SetValue("Icon", executablePath + ",0");
                // %1 必须带引号：路径里有空格时不带引号会被拆成两个参数。
                key.CreateSubKey("command")!
                    .SetValue(string.Empty, "\"" + executablePath + "\" \"%1\" " + verb.Switch);
            }
        }
    }

    /// <summary>撤销：删掉两个范围里属于我们的动词子树。</summary>
    public void Remove()
    {
        foreach (var (scope, _) in Scopes(forFiles: true, forFolders: true))
        {
            using var shell = Registry.CurrentUser.OpenSubKey(ClassesRoot + scope + @"\shell", writable: true);
            if (shell is null)
            {
                continue;
            }

            foreach (var name in shell.GetSubKeyNames())
            {
                if (name.StartsWith(VerbPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    shell.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                }
            }
        }
    }

    /// <summary>某个范围的压缩动词是否已装好（设置页据此回显状态，而不是凭设置文件猜）。</summary>
    public bool IsInstalled(string scope)
    {
        using var shell = Registry.CurrentUser.OpenSubKey(ClassesRoot + scope + @"\shell");
        if (shell is null)
        {
            return false;
        }

        // 写成循环而不是 LINQ：平台兼容性分析器不会把外层的 IsWindows() 判断带进 lambda 里。
        foreach (var name in shell.GetSubKeyNames())
        {
            if (name.StartsWith(VerbPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>让资源管理器重建菜单，否则要等 explorer 重启才看得见。</summary>
    public void NotifyChanged() => SHChangeNotify(ShellAssocChanged, ShellFlushNow, IntPtr.Zero, IntPtr.Zero);

    private const int ShellAssocChanged = 0x08000000;
    private const uint ShellFlushNow = 0x0002;

    private static IEnumerable<(string Scope, bool Enabled)> Scopes(bool forFiles, bool forFolders)
    {
        yield return (FilesScope, forFiles);
        yield return (FoldersScope, forFolders);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
