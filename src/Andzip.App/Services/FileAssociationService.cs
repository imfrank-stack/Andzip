using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 把选中的格式登记到 Windows，并让它成为双击打开的默认程序（HKCU，不需要管理员权限）。
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
    private const string FileExtsRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\";
    private const int ShellAssocChanged = 0x08000000;
    private const uint ShellFlushNow = 0x0002;

    /// <summary>
    /// 登记一个格式：写 ProgID 子树、挂进「打开方式」、把扩展名默认值指过来。可重复调用（幂等）。
    /// </summary>
    /// <returns>接管前的默认值（用于以后还原）与被 UserChoice 挡住、需要去系统设置里手选的扩展名。</returns>
    public AssociationChange Register(FileAssociation association, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var progId = ProgId(association);
        using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + progId))
        {
            key.SetValue(string.Empty, "Andzip " + association.Label);
            WriteCommands(key, executablePath);
        }

        var captured = new List<AssociationOriginal>();
        var blocked = new List<string>();

        foreach (var extension in association.Extensions)
        {
            using (var openWith = Registry.CurrentUser.CreateSubKey(
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
        Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + progId, throwOnMissingSubKey: false);

        foreach (var extension in association.Extensions)
        {
            using (var openWith = Registry.CurrentUser.OpenSubKey(
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
    /// 修复所有 <c>Andzip.*</c> ProgID 里的命令行，指向当前这个 exe；返回改动数量。
    /// <para>
    /// 为什么需要它：绿色版的文件名带版本号（<c>Andzip-v0.9.2-win-x64.exe</c>），升级之后注册表里
    /// 那条命令还指着上一个文件 —— 老 exe 不认 <c>/extract</c>，用户看到的就是「设了没用」。
    /// 启动时顺手把自己命名空间下的键校正过来，用户就不必每次升级都重做一遍关联。
    /// 只动 <c>Andzip.*</c>，别的软件的 ProgID 一概不碰。
    /// </para>
    /// </summary>
    public int RepairCommands(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classes is null)
        {
            return 0;
        }

        var fixedCount = 0;
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

            // 默认动词一律回到 open：双击的行为由「常规 → 双击」设置决定，不靠动词区分。
            WriteCommands(progId!, executablePath);
            fixedCount++;
        }

        return fixedCount;
    }

    /// <summary>写全一个 ProgID 的两个动词，并把默认动词固定为 <c>open</c>。</summary>
    private static void WriteCommands(RegistryKey progId, string executablePath)
    {
        using var shell = progId.CreateSubKey("shell")!;
        shell.SetValue(string.Empty, "open");

        // %1 必须带引号：路径里有空格时不带引号会被拆成两个参数。
        shell.CreateSubKey(@"open\command")!.SetValue(string.Empty, Quote(executablePath) + " \"%1\"");
        shell.CreateSubKey(@"extract\command")!.SetValue(string.Empty, Quote(executablePath) + " \"%1\" " + SettingKeys.ExtractSwitch);
        progId.CreateSubKey("DefaultIcon")!.SetValue(string.Empty, executablePath + ",0");
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
            using var openWith = Registry.CurrentUser.OpenSubKey(ClassesRoot + "." + extension + @"\OpenWithProgids");
            if (openWith is not null && Array.IndexOf(openWith.GetValueNames(), progId) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>让资源管理器立刻刷新关联，否则要等 shell 重启才看得见。</summary>
    public void NotifyChanged() => SHChangeNotify(ShellAssocChanged, ShellFlushNow, IntPtr.Zero, IntPtr.Zero);

    public static string ProgId(FileAssociation association) => ProgIdPrefix + association.Id;

    /// <summary>把扩展名默认值指到我们；返回改动前它是什么（没设过时为空串）。</summary>
    private static string TakeDefault(string extension, string progId)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + "." + extension);
        var previous = key.GetValue(string.Empty) as string ?? string.Empty;

        // 已经是我们的了：不要把 Andzip.xxx 当成“原值”记下来，否则撤销时会把自己写回去。
        if (previous.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        key.SetValue(string.Empty, progId, RegistryValueKind.String);
        return previous;
    }

    private static void RestoreDefault(string extension, string progId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassesRoot + "." + extension, writable: true);
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

    /// <summary>用户显式选过默认程序时 Windows 写的 ProgID；没有则 null。</summary>
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
