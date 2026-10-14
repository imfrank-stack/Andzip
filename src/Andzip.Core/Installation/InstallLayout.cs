namespace Andzip.Core.Installation;

/// <summary>一次"正常安装"要落下的每一件东西，以及它们落在哪里。</summary>
/// <param name="InstallDirectory">程序文件所在目录（默认是每个用户自己的，不需要管理员）。</param>
/// <param name="ExecutablePath">安装后的 exe 全路径。文件关联、快捷方式、卸载表都指向它。</param>
/// <param name="StartMenuShortcut">开始菜单那个快捷方式。</param>
/// <param name="StartMenuUninstallShortcut">开始菜单里那一个「卸载 Andzip」。名字来自语言包，
/// 所以卸载时要按注册表里记下的那个路径收回（见 <c>InstallationService</c>）。</param>
/// <param name="DesktopShortcut">桌面快捷方式；为 null 表示这台机器的桌面目录取不到，跳过。</param>
/// <param name="UninstallKey">注册表「应用和功能」那一条的键名（HKCU 下，不需要管理员）。</param>
public sealed record InstallPlan(
    string InstallDirectory,
    string ExecutablePath,
    string StartMenuShortcut,
    string StartMenuUninstallShortcut,
    string? DesktopShortcut,
    string UninstallKey)
{
    /// <summary>卸载时要按同一个名字找回自己写过的 ProgId（<c>Andzip.*</c>）。</summary>
    public const string ProgIdPrefix = "Andzip.";
}

/// <summary>
/// 安装路径的计算。<strong>纯字符串</strong>：目录从外面传进来，
/// 所以"装到哪、快捷方式叫什么、卸载表写在哪"这些事实能在任何机器上被测试钉住，
/// 不需要真的装一次。
/// <para>为什么默认装 <c>%LOCALAPPDATA%\Programs</c>：<c>Program Files</c> 要管理员，
/// 而 Andzip 整条产品线（文件关联、右键动词、设置文件）都是按"当前用户"设计的，
/// 没有一处需要写机器级配置。用户级目录同时避开了 UAC 虚拟化那一堆坑。</para>
/// <para>向导里那个位置可以改（<paramref name="installDirectory"/>），但<strong>只有程序文件跟着走</strong>：
/// 快捷方式与卸载表仍在当前用户目录下 —— 那两处本来就不需要管理员，把它们也搬到用户选的地方
/// 只会让"装到 D 盘"变成"要管理员"。</para>
/// </summary>
public static class InstallLayout
{
    public const string AppFolder = "Andzip";
    public const string ExeName = "Andzip.exe";

    /// <summary>安装器那一份 exe 的名字前缀：同一个二进制，换个名字跑就是安装界面。</summary>
    public const string InstallerFileNamePrefix = "andzip-setup";

    public const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Andzip";

    /// <param name="localAppData">通常是 <c>%LOCALAPPDATA%</c>。</param>
    /// <param name="roamingAppData">通常是 <c>%APPDATA%</c>，开始菜单在它下面。</param>
    /// <param name="desktop">桌面目录，取不到时传 null（就不建桌面快捷方式）。</param>
    /// <param name="installDirectory">用户在向导里选的位置；null 或空白用默认的
    /// <c>%LOCALAPPDATA%\Programs\Andzip</c>。</param>
    /// <param name="uninstallShortcutName">开始菜单里那一个卸载快捷方式的名字（不含扩展名），
    /// 来自语言包。null 用英文兜底 —— 只有不经过界面的调用（测试）会走到这个兜底。</param>
    public static InstallPlan Plan(
        string localAppData,
        string roamingAppData,
        string? desktop,
        string? installDirectory = null,
        string? uninstallShortcutName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);

        var directory = string.IsNullOrWhiteSpace(installDirectory)
            ? Path.Combine(localAppData, "Programs", AppFolder)
            // 用户填的路径先归一化：带尾斜杠、写成相对路径、混着 / 与 \ 这几种写法
            // 都会一路传到注册表的 InstallLocation 里，卸载时按它找回自己就对不上。
            : InstallTarget.Normalize(installDirectory)
                ?? throw new ArgumentException($"这个路径没法归一化：{installDirectory}", nameof(installDirectory));

        var programs = Path.Combine(
            roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs");

        return new InstallPlan(
            directory,
            Path.Combine(directory, ExeName),
            Path.Combine(programs, AppFolder + ".lnk"),
            Path.Combine(programs, ShortcutFile(uninstallShortcutName ?? $"Uninstall {AppFolder}")),
            desktop is null ? null : Path.Combine(desktop, AppFolder + ".lnk"),
            UninstallKey);
    }

    /// <summary>
    /// 快捷方式的文件名来自语言包 —— 翻译里出现冒号、斜杠是可能的，而那种名字保存不下来，
    /// 症状是"开始菜单里没有卸载那一项"且没有任何报错。所以在这里换成空格。
    /// </summary>
    private static string ShortcutFile(string label)
    {
        var name = label.Trim();
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, ' ');
        }

        return name + ".lnk";
    }

    /// <summary>
    /// 这个 exe 是不是"以安装器的身份"被运行的 —— 判据是<strong>它自己的文件名</strong>。
    /// <para>为什么不看命令行参数：用户是从资源管理器双击 <c>Andzip-Setup-x.y.z.exe</c>，
    /// 没有机会带参数；而"同一个二进制，改名即安装器"意味着发布只需要出一个文件，
    /// 安装器不会和应用版本不一致（这是这类"绿色版顺手带个安装"最常见的坑）。</para>
    /// </summary>
    public static bool IsInstallerFile(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        return name.StartsWith(InstallerFileNamePrefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 向导里「选定安装位置」那一页要报的三个事实，以及安装进行中的每一步。
/// <para>都写成<strong>能单独测</strong>的函数：这一页上的数字是用户决定装不装、装到哪的依据，
/// 报错一个数量级（比如把字节当成 KB）比不报更糟。</para>
/// </summary>
public static class InstallTarget
{
    /// <summary>可写性探针的文件名（点开头，写完就删）。</summary>
    private const string Probe = ".andzip-write-probe";

    /// <summary>
    /// 复制那一个 exe 所需的字节。<strong>没有估的成分</strong>：安装就是复制这一个文件，
    /// 所以"所需空间"就是它的实际大小。
    /// </summary>
    public static long RequiredBytes(string sourceExe) => new FileInfo(sourceExe).Length;

    /// <summary>把用户填的路径归一化成绝对路径（去掉尾斜杠、把 <c>/</c> 换成 <c>\</c>）。填得不完整时返回 null。</summary>
    public static string? Normalize(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));

            // GetFullPath 对 <> 这些是宽容的（实测：原样返回），而 GetInvalidPathChars 里
            // 也没有它们 —— 只能按段拿文件名的字符集量一遍。后面真要落盘时
            // File.Copy 与 .lnk 的 Save 都会抛，在这一处就拒掉，向导才说得清"这个位置不能用"。
            foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar))
            {
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return null;
                }
            }

            return full;
        }
        catch (ArgumentException)
        {
            // 驱动器号写错、UNC 只到主机名 —— 都算"这还不是一个能用的路径"
            return null;
        }
    }

    /// <summary>
    /// 目标所在卷还剩多少字节。取不到返回 null（路径还没打全、U 盘没插、UNC 共享），
    /// 界面据此说"无法确定"而不是报一个 0 —— 一个假的"剩余 0"会让人以为盘满了。
    /// </summary>
    public static long? AvailableBytes(string? directory)
    {
        if (Normalize(directory) is not { } full)
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 这个位置写不写得进去：往<strong>最近的已存在祖先目录</strong>试建一个探针文件再删掉。
    /// <para>为什么不直接 <c>Directory.CreateDirectory</c> 试：用户在向导里改路径时一路打字，
    /// 每改一次就建出一层空目录，取消安装之后盘上留下一堆空壳。探祖先得到的答案是一样的
    /// （建子目录要的权限与在祖先里建文件是同一份），却什么都不留下。</para>
    /// <para>这条同时是"装到 <c>Program Files</c> 会失败"的提前告知：那一处要管理员，
    /// 探针写不进去，于是点「下一步」时就说明白，而不是进度条走到一半再报错。</para>
    /// </summary>
    public static bool IsWritable(string? directory)
    {
        var dir = Normalize(directory);

        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(dir))
            {
                var probe = Path.Combine(dir, Probe);
                try
                {
                    File.WriteAllBytes(probe, []);
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }

                try
                {
                    File.Delete(probe);
                }
                catch (IOException)
                {
                    // 杀软把探针文件多留了一会儿 —— 位置是能写的，别让这一下把「下一步」挡掉
                }
                catch (UnauthorizedAccessException)
                {
                }

                return true;
            }

            dir = Normalize(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)));
        }

        return false;
    }
}

/// <summary>
/// 安装/卸载进行中的一步：界面上该说哪一句，以及这根条此刻走到百分之几。
/// <para>百分比由<strong>做事的那一方</strong>算：它知道这次到底有几步（勾了关联才多一步），
/// 让界面自己数会把"没勾右键菜单"那一步也算进长度里。</para>
/// </summary>
/// <param name="MessageKey">语言包里的键（<c>TEXT_SETUP_STEP_*</c>）。</param>
/// <param name="Percent">这一步<strong>开始</strong>时的进度，0-100。</param>
public sealed record ProgressStep(string MessageKey, int Percent);
