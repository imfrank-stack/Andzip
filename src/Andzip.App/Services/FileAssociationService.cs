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
    /// 注册表里我们那一 namespace 的命令行，指着的<strong>是不是这正在跑着的这一个 exe</strong>。
    /// <para>改设置时先问这一句，是因为 <c>RepairCommands</c> 写的是"当前这个 exe 的路径"：
    /// 装好的那一份、桌面上双击的那一份绿色版、开发目录里那一份、headless 用例的 testhost，
    /// 都是同一个类的不同文件。谁跑一下就把关联抢过去，用户看到的正是"我明明装好了，
    /// 双击却去开另一个文件"，而界面测试会在开发机上留下一个指向 testhost 的关联。</para>
    /// </summary>
    public bool CommandsPointAt(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var root = Root();
        using var classes = root.OpenSubKey(@"Software\Classes");
        if (classes is null)
        {
            return false;
        }

        foreach (var name in classes.GetSubKeyNames())
        {
            if (!name.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var command = classes.OpenSubKey(name + @"\shell\open\command");
            if (command?.GetValue(string.Empty) is string written
                && written.Contains(executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 收掉<strong>命令行里写着这个 exe</strong> 的那些 <c>Andzip.*</c> 登记：ProgID 子树、扩展名上的
    /// 「打开方式」条目，以及指过来的扩展名默认值。返回收掉几棵 ProgID。
    /// <para><strong>卸载走这一条，而不是"照卸载表里那个勾决定要不要收关联"</strong>：关联也可能是
    /// 用户后来自己在「设置 → 文件关联」里勾上的，那时安装器一个开关都没记，卸载照"当初没勾"
    /// 就什么都不收，
    /// 于是卸完之后 <c>HKCU\Software\Classes\.zip</c> 还指着一棵已经没有 exe 的 ProgID ——
    /// 右键菜单里 Andzip 还在，双击一个压缩包却什么都不会发生（用户 2026-10-16 报的「卸载不干净」）。</para>
    /// <para>判据是命令行里那个 exe 的路径，所以<strong>碰不到别的软件</strong>的登记，
    /// 也碰不到另一份 Andzip（另一处的 exe 在别的目录里）。</para>
    /// <para>扩展名默认值优先按记录还原（<paramref name="restoreDefaults"/>），没记录就直接删掉那个值：
    /// 留一个指向死 ProgID 的默认值比没有默认值更糟 —— 没有时系统会问"用什么打开"，
    /// 指着死的则是双击之后毫无反应。</para>
    /// </summary>
    public int RemoveOwnedBy(string executablePath, IReadOnlyList<AssociationOriginal> restoreDefaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var root = Root();
        using var classes = root.OpenSubKey(@"Software\Classes", writable: true);
        if (classes is null)
        {
            return 0;
        }

        // 第一趟：哪些 Andzip.* 是我们这一次要收的那一个 exe 的。
        var owned = new List<string>();
        foreach (var name in classes.GetSubKeyNames())
        {
            if (name.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase)
                && PointsAt(classes.OpenSubKey(name), executablePath))
            {
                owned.Add(name);
            }
        }

        if (owned.Count == 0)
        {
            return 0;
        }

        // 第二趟：把挂在这几棵上的扩展名摘下来。整棵 ProgID 删掉之后，
        // 一个还指着它的默认值就是一个双击没有反应的死指针。
        foreach (var extension in classes.GetSubKeyNames())
        {
            if (!extension.StartsWith('.'))
            {
                continue;
            }

            var progId = DefaultOf(classes.OpenSubKey(extension));

            using (var openWith = classes.OpenSubKey(extension + @"\OpenWithProgids", writable: true))
            {
                if (openWith is not null)
                {
                    foreach (var listed in owned.Where(
                        id => Array.IndexOf(openWith.GetValueNames(), id) >= 0))
                    {
                        openWith.DeleteValue(listed, throwOnMissingValue: false);
                    }
                }
            }

            if (progId is null || !owned.Contains(progId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var original = restoreDefaults.FirstOrDefault(
                item => string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase));

            var extensionKey = classes.OpenSubKey(extension, writable: true);
            if (original is { ProgId.Length: > 0 })
            {
                extensionKey?.SetValue(string.Empty, original.ProgId, RegistryValueKind.String);
            }
            else
            {
                extensionKey?.DeleteValue(string.Empty, throwOnMissingValue: false);
            }

            extensionKey?.Dispose();
        }

        foreach (var name in owned)
        {
            classes.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }

        _notifyShell();
        return owned.Count;
    }

    /// <summary>
    /// 这棵 ProgID 里有没有任何一条动词的命令行写着这个 exe。
    /// <para>看所有动词而不只是 <c>open</c>：老版本写过 <c>shell\extract\command</c>，
    /// 只认一条会漏掉"键还在、命令行指着我们"的那些。</para>
    /// </summary>
    private static bool PointsAt(RegistryKey? progId, string executablePath)
    {
        using (progId)
        {
            if (progId?.OpenSubKey("shell") is not { } shell)
            {
                return false;
            }

            using (shell)
            {
                foreach (var verb in shell.GetSubKeyNames())
                {
                    if (DefaultOf(shell.OpenSubKey(verb + @"\command")) is { } command
                        && command.Contains(executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string? DefaultOf(RegistryKey? key)
    {
        using (key)
        {
            return key?.GetValue(string.Empty) as string;
        }
    }

    /// <summary>
    /// 扫掉<strong>"扩展名默认值指着一棵已经不存在的 <c>Andzip.*</c> ProgID"</strong>的那些键，
    /// 两个作用域各扫一遍。返回收回几处。
    /// <para><strong>为什么 <see cref="RemoveOwnedBy"/> 之外还要这一条</strong>：那一条只在自己那一棵
    /// 根里按 exe 收，而"死指针"可能是<strong>老版本</strong>留下的 —— 它们调的是
    /// <c>Unregister(association, [])</c>，还原表传的是空表，于是"还原到接管前"那一步整个跳过。
    /// 机器级那一份卸完之后，<c>HKLM\Software\Classes\.zip</c> 还写着 <c>Andzip.zip</c>，
    /// 而那一棵 ProgID 已经没了（这台机器上 2.4.1 那次留下的就是这个样子）。
    /// 这一条不依赖"谁写的"，只问"这个指针现在还有没有去处"，所以能把老账扫干净。</para>
    /// <para>ProgID 在<strong>不在</strong>要两个根都问 —— 只问自己那一个会误删另一处安装的活指针。
    /// 写不动的那个根（不提权的 HKLM）跳过：本程序从不悄悄提权，也从不假装收干净了。</para>
    /// <para><strong>调用方必须先证明"这一次确实收掉过我们自己的登记"</strong>（见
    /// <see cref="InstallationService.Uninstall"/> 里那个 <c>removed &gt; 0</c> 闸门）：这一条不按
    /// exe 认人，无条件调用会让一次没勾关联的装/卸往返去改这台机器真的关联表 ——
    /// 而测试里全是那样的往返（这一条第一版就是这么把 6 条卸载用例打红的）。</para>
    /// </summary>
    public int RemoveDangling(IReadOnlyList<AssociationOriginal> restoreDefaults)
    {
        var swept = 0;

        foreach (var scope in new[] { InstallScope.CurrentUser, InstallScope.AllUsers })
        {
            try
            {
                swept += SweepOneRoot(scope, restoreDefaults);
            }
            catch (Exception)
            {
                // 这一棵根开不动或写不动（没有管理员权限的 HKLM、被策略挡住的键）—— 跳过它。
                // 不是失败：机器级那一份的死指针本来就该由提权之后那一次卸载去收，
                // 而本程序从不悄悄提权。
            }
        }

        if (swept > 0)
        {
            _notifyShell();
        }

        return swept;
    }

    private int SweepOneRoot(InstallScope scope, IReadOnlyList<AssociationOriginal> restoreDefaults)
    {
        var swept = 0;
        using var root = _root(scope);
        using var classes = root.OpenSubKey(@"Software\Classes", writable: true);
        if (classes is null)
        {
            return 0;
        }

        foreach (var extension in classes.GetSubKeyNames())
        {
            if (!extension.StartsWith('.'))
            {
                continue;
            }

            if (DefaultOf(classes.OpenSubKey(extension)) is not { } progId
                || !progId.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ProgIdExistsAnywhere(progId))
            {
                continue;
            }

            var original = restoreDefaults.FirstOrDefault(
                item => string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase));

            using var key = classes.OpenSubKey(extension, writable: true);
            if (key is null)
            {
                continue;
            }

            if (original is { ProgId.Length: > 0 })
            {
                key.SetValue(string.Empty, original.ProgId, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(string.Empty, throwOnMissingValue: false);
            }

            swept++;
        }

        return swept;
    }

    /// <summary>开一棵根，开不动就返回 null（HKLM 在没有管理员权限时抛 SecurityException）。</summary>
    private RegistryKey? OpenQuietly(InstallScope scope)
    {
        try
        {
            return _root(scope);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>这棵 ProgID 在<strong>两个根里的任何一个</strong>还存在吗。只问自己那一个会误删另一处安装的活指针。</summary>
    private bool ProgIdExistsAnywhere(string progId)
    {
        foreach (var scope in new[] { InstallScope.CurrentUser, InstallScope.AllUsers })
        {
            using var root = OpenQuietly(scope);
            if (root is null)
            {
                continue;
            }

            using var found = root.OpenSubKey(ClassesRoot + progId);
            if (found is not null)
            {
                return true;
            }
        }

        return false;
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
    /// 关掉之后 ProgID 里只剩双击那一条动词。</para>
    /// <para><see cref="ExtractOnDoubleClick"/> 对应「设置 → 解压 → 双击压缩包文件时」，
    /// 它决定 <c>shell\open</c> 这一行在菜单上叫什么：<strong>菜单写的必须就是双击真的做的事</strong>
    /// （0.9.12 用户报过反过来的那次：行写着「用 Andzip 打开」，点下去却去解压）。</para>
    /// </summary>
    internal sealed record ExtractionVerbLabels(string Open, string Extract, string ExtractDelete)
    {
        public bool ShowExtractVerbs { get; init; } = true;

        public bool ExtractOnDoubleClick { get; init; }

        /// <summary>双击=直接解压时，<c>shell\open</c> 那一行改叫这个名字（来自语言包）。</summary>
        public string ExtractDirectly { get; init; } = string.Empty;

        /// <summary><c>shell\open</c> 在菜单上的文字。没给"直接解压"那句时退回「用 Andzip 打开」。</summary>
        public string DoubleClickLabel =>
            ExtractOnDoubleClick && ExtractDirectly.Length > 0 ? ExtractDirectly : Open;
    }

    /// <summary>
    /// 写全一个 ProgID 的动词，并把默认动词固定为 <c>open</c>。
    /// <para>
    /// 命令行统一带 <c>/task=…</c>（见 <see cref="Core.Archive.ShellTasks"/>）：注册表只负责
    /// 「把这个文件交给 Andzip 做某件事」，怎么做由参数说清楚，不靠某个动词键是否存在。
    /// 唯一的例外是双击那条 <c>shell\open</c> —— 它不带开关，做什么由「解压 → 双击压缩包文件时」
    /// 决定，改设置因此不用重写所有 ProgID。
    /// </para>
    /// <para>
    /// <strong><c>shell\open</c> 一律可见，谁都不许给它加 <c>ProgrammaticAccessOnly</c></strong>。
    /// 0.10.0 给它加过，理由很正当：菜单上不想出现两行「用 Andzip 打开」。代价是这条藏起来之后
    /// <strong>它就不当双击的默认动词了</strong> —— 资源管理器往下找第一条看得见的动词，找到的是
    /// <c>shell\andzip-open</c>，而那一条命令行里写死了 <c>/task=open</c>。于是从 0.10.0 起
    /// 「双击 = 解压那一档」（当时界面上叫「直接解压到当前文件夹」）在真机上永远不生效，
    /// 改设置、改注册表默认动词都没用。
    /// 这一层只有真的走 shell 的动词表才看得见：本机实测过，藏起来时
    /// <c>Shell.Application</c> 的 <c>InvokeVerb()</c>（双击那一下）落到 <c>/task=open</c>，
    /// 把值去掉之后同一次调用就地铺开；而 <c>Invoke-Item</c> 与 <c>Process.Start</c> 这类
    /// 直接读 <c>shell</c> 默认值的走法两种情况下都对 —— 之前三次"修好了"都是被它骗过去的。
    /// </para>
    /// <para>
    /// 重复的那一行改由「用 Andzip 打开」<strong>自己</strong>让位：双击本来就是打开时，
    /// <c>shell\open</c> 已经叫这个名字，<c>andzip-open</c> 藏起来；双击是直接解压时两行名字不同，
    /// 都留着，那条"永远在软件里打开"的承诺还在菜单上。
    /// </para>
    /// </summary>
    /// <returns>有没有真的改过（值全都一样时一笔都不写）。</returns>
    private static bool WriteCommands(RegistryKey progId, string executablePath, ExtractionVerbLabels labels)
    {
        var changed = false;

        using var shell = progId.CreateSubKey("shell")!;
        changed |= RegistryWrite.SetIfDifferent(shell, string.Empty, "open");

        changed |= WriteVerb(shell, "open", labels.DoubleClickLabel, executablePath,
            Quote(executablePath) + " \"%1\"");
        changed |= WriteVerb(shell, "andzip-open", labels.Open, executablePath,
            Quote(executablePath) + " \"%1\" " + Core.Archive.ShellTasks.Switch(Core.Archive.ShellTask.Open),
            hidden: !labels.ExtractOnDoubleClick);
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
