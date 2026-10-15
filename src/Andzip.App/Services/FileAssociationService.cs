using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Archive;
using Andzip.Core.Installation;
using Andzip.Core.Settings;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 把选中的格式登记到 Windows，并让它成为双击打开的默认程序。
/// <para>默认登记到 <c>HKCU\Software\Classes</c>（不需要管理员权限）；构造时给
/// <c>InstallScope.AllUsers</c> 就登记到 <c>HKLM\Software\Classes</c>（要管理员，见
/// <c>Elevation</c>）。两处的相对键路径是同一个，所以只有根不一样。</para>
/// <para>
/// 两处都要写，缺一不可：
/// </para>
/// <list type="bullet">
///   <item>扩展名键的默认值 —— 决定双击打开用谁；</item>
///   <item><c>OpenWithProgids</c> —— 决定右键「打开方式」列表里有没有我们。</item>
/// </list>
/// <para>
/// 覆盖默认值之前先把原值记进设置（<see cref="AssociationOriginals"/>），取消勾选时按记录还原，
/// 没记到的一律不动。撤销是干净的：每个格式一棵独立的 <c>Andzip.&lt;id&gt;</c> ProgID 子树，
/// 整棵删掉，扩展名上只删我们自己挂的值，不碰别人的。
/// </para>
/// <para>
/// <strong>做不了也不做的一件事</strong>：伪造 <c>FileExts\&lt;ext&gt;\UserChoice</c> 里的哈希。
/// 那是 Windows 10 1709 之后防劫持默认应用的保护，算哈希的密钥是每用户随机的，绕开它属于
/// 破解系统安全机制，而且随版本变化随时会失效。所以这里老老实实写 <c>HKCU\Software\Classes</c>：
/// 没有 UserChoice 的扩展名（多数压缩包格式属于这种）立刻生效；有的会被挡下来，
/// 由 <see cref="Register"/> 报给界面提示用户去系统设置里选。
/// </para>
/// <para>
/// <c>HKEY_CLASSES_ROOT</c> 只是 HKCU 与 HKLM 的合并视图，所以这里从不写 HKCR ——
/// 写哪一半由作用域决定，读"某个扩展名现在默认是谁"时才用合并视图。
/// <strong>唯一的例外是 <c>FileExts\&lt;ext&gt;\UserChoice</c>：那是每用户自己的选择，
/// 机器级安装时也要读当前用户的这一份</strong>（读它只为判断"我们写了却被它挡住"，不需要写权限）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FileAssociationService(InstallScope scope = InstallScope.CurrentUser,
    Func<InstallScope, RegistryKey>? root = null,
    Action? notifyShell = null)
{
    private readonly InstallScope _scope = scope;

    /// <summary>这一次登记写进哪个根。用完即弃：<c>OpenBaseKey</c> 只是拿句柄，不读内容。</summary>
    private RegistryKey Root() => _root(_scope);

    /// <summary>
    /// 作用域 → 注册表根。默认按 <see cref="RegistryScope"/> 走；测试与自检换成自己的探针子树，
    /// 于是"校正关联"这条会重写一堆 <c>Andzip.*</c> 的路径可以整条真跑一遍而不碰开发机的关联。
    /// </summary>
    private readonly Func<InstallScope, RegistryKey> _root = root ?? RegistryScope.Open;

    /// <summary>
    /// 让 shell 重读关联的那一下。默认就是真的广播；测试注入一个计数器，
    /// 因为"有没有广播"正是这一档要钉住的东西，而广播本身会刷新开发机的桌面图标。
    /// </summary>
    private readonly Action _notifyShell = notifyShell ?? NotifyShell;

    /// <summary>ProgID 前缀。设置存储与注册表都靠它识别“哪些是 Andzip 登记的”。</summary>
    public const string ProgIdPrefix = "Andzip.";

    private const string ClassesRoot = @"Software\Classes\";
    private const string FileExtsRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\";
    private const int ShellAssocChanged = 0x08000000;
    private const uint ShellFlushNow = 0x0002;

    /// <summary>
    /// 登记一个格式：写 ProgID 子树、挂进「打开方式」、把扩展名默认值指过来。可重复调用（幂等）。
    /// </summary>
    /// <returns>接管前的默认值（用于以后还原）与被 UserChoice 挡住、需要去系统设置里手选的扩展名。</returns>
    public AssociationChange Register(FileAssociation association, string executablePath, ExtractionVerbLabels labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(labels);

        var progId = ProgId(association);
        using var root = Root();
        using (var key = root.CreateSubKey(ClassesRoot + progId))
        {
            key.SetValue(string.Empty, "Andzip " + association.Label);
            WriteCommands(key, executablePath, labels);
        }

        var captured = new List<AssociationOriginal>();
        var blocked = new List<string>();

        foreach (var extension in association.Extensions)
        {
            using (var openWith = root.CreateSubKey(
                ClassesRoot + "." + extension + @"\OpenWithProgids"))
            {
                openWith.SetValue(progId, string.Empty, RegistryValueKind.String);
            }

            captured.Add(new AssociationOriginal(extension, TakeDefault(extension, progId)));

            if (ProtectedDefault(extension) is { } chooser && !chooser.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add(extension);
            }
        }

        return new AssociationChange(captured, blocked);
    }

    /// <summary>
    /// 取消登记：删掉我们的 ProgID 与「打开方式」条目，并把默认值还原到接管前。
    /// </summary>
    /// <param name="restoreDefaults">只还原记录在案的那些；没记录说明不是我们改的，不动。</param>
    public void Unregister(FileAssociation association, IReadOnlyList<AssociationOriginal> restoreDefaults)
    {
        var progId = ProgId(association);
        using var root = Root();
        root.DeleteSubKeyTree(ClassesRoot + progId, throwOnMissingSubKey: false);

        foreach (var extension in association.Extensions)
        {
            using (var openWith = root.OpenSubKey(
                ClassesRoot + "." + extension + @"\OpenWithProgids", writable: true))
            {
                if (openWith is not null && Array.IndexOf(openWith.GetValueNames(), progId) >= 0)
                {
                    openWith.DeleteValue(progId, throwOnMissingValue: false);
                }
            }

            var original = restoreDefaults.FirstOrDefault(
                item => string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase));

            if (original is null)
            {
                continue;
            }

            RestoreDefault(extension, original.ProgId);
        }
    }

    /// <summary>
    /// 修复所有 <c>Andzip.*</c> ProgID 里的命令行，指向当前这个 exe；
    /// 返回<strong>真的改过</strong>几个，并在改过的时候让 shell 重读一次。
    /// <para>
    /// 为什么需要它：绿色版的文件名带版本号（<c>Andzip-v0.9.2-win-x64.exe</c>），升级之后注册表里
    /// 那条命令还指着上一个文件 —— 老 exe 不认 <c>/extract</c>，用户看到的就是「设了没用」。
    /// 启动时顺手把自己命名空间下的键校正过来，用户就不必每次升级都重做一遍关联。
    /// 只动 <c>Andzip.*</c>，别的软件的 ProgID 一概不碰。
    /// </para>
    /// <para>
    /// <strong>返回的是"真改过"，不是"看过几个"，而且广播只跟着前者走</strong>（3.2.1）：
    /// 这条自愈路径每次通过关联启动都会跑，而原先的计数数的是"我重写了几个" ——
    /// 于是每次双击压缩包/右键解压都会发一次全局 <c>SHCNE_ASSOCCHANGED</c>，
    /// 用户看到的现象是<strong>屏幕上所有图标整片闪一下</strong>（3.2.0 报的）。
    /// 真机上量的：一次这样的启动会把 <c>Andzip.zip</c> 与 <c>.zip</c> 两个键<strong>都写一遍</strong>
    /// （值一个字都没变），紧接着 shell 重写了图标缓存 <c>iconcache_idx.db</c>。
    /// 现在值相同就不写、没写就不广播。
    /// </para>
    /// </summary>
    public int RepairCommands(string executablePath, ExtractionVerbLabels labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(labels);

        using var root = Root();
        using var classes = root.OpenSubKey(@"Software\Classes", writable: true);
        if (classes is null)
        {
            return 0;
        }

        var repaired = 0;
        foreach (var name in classes.GetSubKeyNames())
        {
            if (!name.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var progId = classes.OpenSubKey(name, writable: true);
            if (progId?.OpenSubKey("shell") is null)
            {
                continue;
            }

            // 默认动词一律回到 open：双击的行为由「解压 → 双击」设置决定，不靠动词区分。
            if (WriteCommands(progId!, executablePath, labels))
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

    /// <summary>
    /// 右键解压菜单的文字与开关（由界面从语言包取好传进来，服务层不认识语言包）。
    /// <para><see cref="ShowExtractVerbs"/> 对应「设置 → 上下文菜单 → 解压菜单项」：
    /// 关掉之后 ProgID 里只留 open 动词，双击仍然按「解压 → 双击」设置走，不受影响。</para>
    /// </summary>
    internal sealed record ExtractionVerbLabels(string Open, string Extract, string ExtractDelete)
    {
        public bool ShowExtractVerbs { get; init; } = true;
    }

    /// <summary>
    /// 写全一个 ProgID 的动词，并把默认动词固定为 <c>open</c>。
    /// <para>
    /// 命令行统一带 <c>/task=…</c>（见 <see cref="Core.Archive.ShellTasks"/>）：注册表只负责
    /// 「把这个文件交给 Andzip 做某件事」，怎么做由参数说清楚，不靠某个动词键是否存在。
    /// </para>
    /// <para>
    /// <strong>双击与右键「打开」是两条键，这是刻意的</strong>。<c>shell\open</c> 是双击要执行的
    /// 那条（<c>shell</c> 的默认值就是它），它<strong>不</strong>带开关，于是"双击干什么"仍然由
    /// 「解压 → 双击压缩包文件时」决定，改设置不用重写所有 ProgID；它带
    /// <c>ProgrammaticAccessOnly</c>，所以不出现在菜单上。菜单里那条「用 Andzip 打开」是
    /// <c>shell\andzip-open</c>，命令行写死 <c>/task=open</c> —— 名字承诺什么就干什么，
    /// 不受双击设置影响（0.9.12 之前两者是同一条，勾了"直接解压"的用户点「打开」得到的是解压）。
    /// </para>
    /// </summary>
    /// <returns>有没有真的改过（值全都一样时一笔都不写）。</returns>
    private static bool WriteCommands(RegistryKey progId, string executablePath, ExtractionVerbLabels labels)
    {
        var changed = false;

        using var shell = progId.CreateSubKey("shell")!;
        changed |= RegistryWrite.SetIfDifferent(shell, string.Empty, "open");

        changed |= WriteVerb(shell, "open", labels.Open, executablePath, Quote(executablePath) + " \"%1\"", hidden: true);
        changed |= WriteVerb(shell, "andzip-open", labels.Open, executablePath,
            Quote(executablePath) + " \"%1\" " + Core.Archive.ShellTasks.Switch(Core.Archive.ShellTask.Open));
        changed |= WriteVerb(shell, "extract", labels.Extract, executablePath,
            Quote(executablePath) + " \"%1\" " + Core.Archive.ShellTasks.Switch(Core.Archive.ShellTask.Extract),
            enabled: labels.ShowExtractVerbs);
        changed |= WriteVerb(shell, "extract-delete", labels.ExtractDelete, executablePath,
            Quote(executablePath) + " \"%1\" " + Core.Archive.ShellTasks.Switch(Core.Archive.ShellTask.ExtractDelete),
            enabled: labels.ShowExtractVerbs);

        // 退役的动词键必须显式删掉：只写新键的话菜单上会同时留着旧的，用户分不清点哪个。
        // auto-extract / extract-here 是 0.9.12 及以前那两条解压，已被单条 extract 取代。
        foreach (var retired in new[] { "auto-extract", "extract-here" })
        {
            changed |= RegistryWrite.DeleteTreeIfPresent(shell, retired);
        }

        using var icon = progId.CreateSubKey("DefaultIcon")!;
        changed |= RegistryWrite.SetIfDifferent(icon, string.Empty, executablePath + ",0");

        return changed;
    }

    /// <returns>有没有真的改过。</returns>
    private static bool WriteVerb(
        RegistryKey shell,
        string name,
        string label,
        string executablePath,
        string command,
        bool enabled = true,
        bool hidden = false)
    {
        // 关掉的一项要真的删掉，不是留着键改文字 —— 菜单上少一项才是用户点的意思。
        if (!enabled)
        {
            return RegistryWrite.DeleteTreeIfPresent(shell, name);
        }

        var changed = false;

        using var verb = shell.CreateSubKey(name)!;
        // 键的默认值就是菜单文字；Icon 让条目旁边带上程序图标而不是空白。
        changed |= RegistryWrite.SetIfDifferent(verb, string.Empty, label);
        changed |= RegistryWrite.SetIfDifferent(verb, "Icon", executablePath + ",0");
        if (hidden)
        {
            // 只被程序（双击、打开方式）调用，不进右键菜单。本机实测过：加上这个值之后
            // Shell.Application 枚举 box.zip 的动词表里就没有这一项了，去掉又回来。
            changed |= RegistryWrite.SetIfDifferent(verb, "ProgrammaticAccessOnly", string.Empty);
        }
        else
        {
            changed |= RegistryWrite.DeleteIfPresent(verb, "ProgrammaticAccessOnly");
        }

        using var commandKey = verb.CreateSubKey("command")!;
        changed |= RegistryWrite.SetIfDifferent(commandKey, string.Empty, command);

        return changed;
    }

    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>
    /// 这个格式当前是否已经登记在系统里。<strong>以注册表为准</strong>，不看设置文件 ——
    /// 关联页要显示的是现实，不是上一次写成功的记录：注册表可能被别的软件改掉、被清理、
    /// 或者被用户手动撤销过，照设置文件回显就会勾着一堆其实没生效的项。
    /// </summary>
    public bool IsRegistered(FileAssociation association)
    {
        var progId = ProgId(association);
        foreach (var extension in association.Extensions)
        {
            using var root = Root();
            using var openWith = root.OpenSubKey(ClassesRoot + "." + extension + @"\OpenWithProgids");
            if (openWith is not null && Array.IndexOf(openWith.GetValueNames(), progId) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 让资源管理器立刻刷新关联，否则要等 shell 重启才看得见。
    /// <para><strong>这是一次全局广播</strong>：它会重建关联与图标缓存，屏幕上的图标整片闪一下。
    /// 所以只在用户真的改了关联、或自愈真的改到了东西时用（见 <see cref="RepairCommands"/>）。</para>
    /// </summary>
    public void NotifyChanged() => _notifyShell();

    private static void NotifyShell() => SHChangeNotify(ShellAssocChanged, ShellFlushNow, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 已经登记、但默认程序仍是别的软件的扩展名 —— 也就是「关联做了、双击却不是 Andzip」。
    /// <para>
    /// 原因是 Windows 10 1709 起的默认应用保护：用户显式选过的程序记在
    /// <c>FileExts\&lt;ext&gt;\UserChoice</c> 里，附一个用每用户密钥算出的哈希，优先级高于
    /// <c>Software\Classes\.&lt;ext&gt;</c> 的默认值。哈希伪造不了（那正是它防的事），
    /// 所以只能把用户送到系统设置里点一次。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ShadowedDefaults(FileAssociation association)
    {
        var shadowed = new List<string>();
        foreach (var extension in association.Extensions)
        {
            if (ProtectedDefault(extension) is { } chooser
                && !chooser.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                shadowed.Add(extension);
            }
        }

        return shadowed;
    }

    public static string ProgId(FileAssociation association) => ProgIdPrefix + association.Id;

    /// <summary>把扩展名默认值指到我们；返回改动前它是什么（没设过时为空串）。</summary>
    private string TakeDefault(string extension, string progId)
    {
        using var root = Root();
        using var key = root.CreateSubKey(ClassesRoot + "." + extension);
        var previous = key.GetValue(string.Empty) as string ?? string.Empty;

        // 已经是我们的了：不要把 Andzip.xxx 当成“原值”记下来，否则撤销时会把自己写回去。
        if (previous.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        key.SetValue(string.Empty, progId, RegistryValueKind.String);
        return previous;
    }

    private void RestoreDefault(string extension, string progId)
    {
        using var root = Root();
        using var key = root.OpenSubKey(ClassesRoot + "." + extension, writable: true);
        if (key is null)
        {
            return;
        }

        // 只有当前仍然指向我们时才敢动：期间可能换过别的默认程序，写回旧值会覆盖新选择。
        if (key.GetValue(string.Empty) as string is not { } current
            || !current.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (progId.Length == 0)
        {
            key.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(string.Empty, progId, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// 用户显式选过默认程序时 Windows 写的 ProgID；没有则 null。
    /// <para><strong>这一条永远读当前用户</strong>，与安装作用域无关：它问的是"这台机器上<em>这个人</em>
    /// 自己点过哪个默认程序"，而那个键（<c>HKCU\...\FileExts\&lt;ext&gt;\UserChoice</c>）
    /// 也只存在于每个用户自己名下。机器级安装挡住没挡住，读的还是运行这一份的用户的选择。</para>
    /// </summary>
    private static string? ProtectedDefault(string extension)
    {
        using var choice = Registry.CurrentUser.OpenSubKey(FileExtsRoot + "." + extension + @"\UserChoice");
        return choice?.GetValue("ProgId") as string;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

/// <summary>一次登记的结果。</summary>
/// <param name="CapturedDefaults">接管前的默认值，调用方必须持久化，否则无法撤销。</param>
/// <param name="BlockedExtensions">被 Windows 的 UserChoice 保护挡住、我们改不动的扩展名。</param>
internal sealed record AssociationChange(
    IReadOnlyList<AssociationOriginal> CapturedDefaults,
    IReadOnlyList<string> BlockedExtensions);
