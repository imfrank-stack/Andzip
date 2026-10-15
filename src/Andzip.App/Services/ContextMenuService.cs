using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Installation;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 资源管理器右键菜单里的「压缩为 …」项，用静态注册表动词实现（不需要 COM shell 扩展）。
/// <para>
/// 文件写在 <c>Software\Classes\*\shell</c> 下，文件夹写在 <c>…\Directory\shell</c> 下，
/// 两个范围各一个开关。键名一律带 <see cref="VerbPrefix"/> 前缀，因此撤销就是把这几个子树删掉，
/// 不碰别的软件加的动词。
/// </para>
/// <para>
/// <strong>写哪个根由作用域决定</strong>：默认 <c>HKCU</c>（当前用户，不需要管理员）；
/// 机器级安装写 <c>HKLM</c>（所有用户，要管理员）。"范围"（<c>*</c> 与 <c>Directory</c>）
/// 与"作用域"（当前用户 / 所有用户）是两件事，所以这里的局部变量一律叫 <c>target</c>。
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
internal sealed class ContextMenuService(InstallScope scope = InstallScope.CurrentUser,
    Func<InstallScope, RegistryKey>? root = null,
    Action? notifyShell = null)
{
    private readonly InstallScope _scope = scope;

    /// <summary>这一次写进哪个根。用完即弃：<c>OpenBaseKey</c> 只是拿句柄，不读内容。</summary>
    private RegistryKey Root() => _root(_scope);

    /// <summary>作用域 → 注册表根；测试换成探针子树，见 <see cref="FileAssociationService"/> 里同一处。</summary>
    private readonly Func<InstallScope, RegistryKey> _root = root ?? RegistryScope.Open;

    /// <summary>让 shell 重读菜单的那一下；测试注入计数器，因为"有没有广播"正是要钉住的东西。</summary>
    private readonly Action _notifyShell = notifyShell ?? NotifyShell;

    /// <summary>我们自己动词的键名前缀，用于识别与清理。</summary>
    public const string VerbPrefix = "Andzip.";

    /// <summary>「所有文件」范围。</summary>
    public const string FilesScope = "*";

    /// <summary>「文件夹」范围。</summary>
    public const string FoldersScope = "Directory";

    private const string ClassesRoot = @"Software\Classes\";

    /// <summary>
    /// 一个要装的压缩动词：注册表键名后缀 + 命令行开关 + 菜单文字。
    /// <para><paramref name="AppliesTo"/> 是给资源管理器的过滤条件，写进动词键的 <c>AppliesTo</c> 值，
    /// 不匹配的文件类型上这一项就不出现 —— 我们靠它把「压缩为 …」从压缩包身上摘掉。
    /// 只在"所有文件"这个范围写，文件夹范围不写（文件夹没有扩展名，本来也不该被过滤掉）。</para>
    /// </summary>
    public sealed record CompressionVerb(string Key, string Switch, string Label, string? AppliesTo = null);

    /// <summary>
    /// 按两个开关写压缩动词。可重复调用（幂等）：每次都先把我们的键清空再写，
    /// 这样在设置里取消勾选的那几项不会残留在菜单上。
    /// </summary>
    public void Install(IReadOnlyList<CompressionVerb> verbs, string executablePath, bool forFiles, bool forFolders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        Remove();

        using var root = Root();
        foreach (var (target, enabled) in Scopes(forFiles, forFolders))
        {
            if (!enabled)
            {
                continue;
            }

            using var shell = root.CreateSubKey(ClassesRoot + target + @"\shell");
            WriteVerbs(shell, verbs, executablePath, filterByExtension: target == FilesScope);
        }
    }

    /// <summary>
    /// 在<strong>已经装了</strong>压缩动词的范围里补齐缺的条目，返回<strong>真的改过</strong>的范围数，
    /// 并在改过的时候让 shell 重读一次菜单。
    /// <para>
    /// 为什么需要它：动词是按条写死的，新版本加一条（比如「压缩到…」对话框）时，老用户注册表里
    /// 只有前两条，而设置页看到"Andzip. 开头的键存在"就以为全装好了 —— 新那条永远不会出现。
    /// 一个范围都没装时什么都不做：没勾过的用户不该被塞进菜单。
    /// </para>
    /// <para>返回"真改过"而不是"看过几个"，理由与 <see cref="FileAssociationService.RepairCommands"/>
    /// 完全一样：这条自愈路径每次通过关联启动都会跑，而广播一次就是<strong>屏幕上所有图标闪一下</strong>。</para>
    /// </summary>
    public int RepairVerbs(string executablePath, IReadOnlyList<CompressionVerb> verbs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var repaired = 0;
        using var root = Root();
        foreach (var (target, _) in Scopes(forFiles: true, forFolders: true))
        {
            if (!IsInstalled(target))
            {
                continue;
            }

            using var shell = root.OpenSubKey(ClassesRoot + target + @"\shell", writable: true);
            if (shell is not null && WriteVerbs(shell, verbs, executablePath, filterByExtension: target == FilesScope))
            {
                repaired++;
            }
        }

        if (repaired > 0)
        {
            _notifyShell();
        }

        return repaired;
    }

    /// <returns>有没有真的改过。</returns>
    private static bool WriteVerbs(
        RegistryKey shell,
        IReadOnlyList<CompressionVerb> verbs,
        string executablePath,
        bool filterByExtension)
    {
        var changed = false;
        var keep = new HashSet<string>(verbs.Select(verb => VerbPrefix + verb.Key), StringComparer.OrdinalIgnoreCase);

        // 先清掉我们自己的、这次不再提供的键。Install() 会先整体 Remove()，所以这一步只为
        // 启动自愈而存在：那条路不能整体清（用户可能只勾了文件夹范围），但退役的动词
        // （0.9.12 撤掉的「压缩为 7Z」）必须消失，否则菜单上是新旧两条并存。
        foreach (var name in shell.GetSubKeyNames())
        {
            if (name.StartsWith(VerbPrefix, StringComparison.OrdinalIgnoreCase) && !keep.Contains(name))
            {
                changed |= RegistryWrite.DeleteTreeIfPresent(shell, name);
            }
        }

        foreach (var verb in verbs)
        {
            using var key = shell.CreateSubKey(VerbPrefix + verb.Key);
            changed |= RegistryWrite.SetIfDifferent(key, string.Empty, verb.Label);
            changed |= RegistryWrite.SetIfDifferent(key, "Icon", executablePath + ",0");
            if (filterByExtension && verb.AppliesTo is { Length: > 0 } filter)
            {
                changed |= RegistryWrite.SetIfDifferent(key, "AppliesTo", filter);
            }
            else
            {
                // 关掉过滤要真的删掉：留着上一次写的值，这一版就还是隐身状态。
                changed |= RegistryWrite.DeleteIfPresent(key, "AppliesTo");
            }

            // %1 必须带引号：路径里有空格时不带引号会被拆成两个参数。
            using var command = key.CreateSubKey("command")!;
            changed |= RegistryWrite.SetIfDifferent(command, string.Empty, "\"" + executablePath + "\" \"%1\" " + verb.Switch);
        }

        return changed;
    }

    /// <summary>撤销：删掉两个范围里属于我们的动词子树。</summary>
    public void Remove()
    {
        using var root = Root();
        foreach (var (target, _) in Scopes(forFiles: true, forFolders: true))
        {
            using var shell = root.OpenSubKey(ClassesRoot + target + @"\shell", writable: true);
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

    /// <summary>某个范围（<c>*</c> / <c>Directory</c>）的压缩动词是否已装好
    /// —— 设置页据此回显状态，而不是凭设置文件猜。作用域由构造时决定。</summary>
    public bool IsInstalled(string target)
    {
        using var root = Root();
        using var shell = root.OpenSubKey(ClassesRoot + target + @"\shell");
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

    /// <summary>
    /// 让资源管理器重建菜单，否则要等 explorer 重启才看得见。
    /// <para><strong>这是一次全局广播</strong>（图标缓存跟着重建、屏幕上图标整片闪一下），
    /// 所以只在真的改了菜单的时候用 —— 见 <see cref="RepairVerbs"/>。</para>
    /// </summary>
    public void NotifyChanged() => _notifyShell();

    private static void NotifyShell() => SHChangeNotify(ShellAssocChanged, ShellFlushNow, IntPtr.Zero, IntPtr.Zero);

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
