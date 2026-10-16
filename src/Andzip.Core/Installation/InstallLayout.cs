namespace Andzip.Core.Installation;

/// <summary>
/// 这一次装到"谁"名下。
/// <para><see cref="CurrentUser"/> 是默认也是推荐：<c>%LOCALAPPDATA%</c> + <c>%APPDATA%</c> 的开始菜单
/// + <c>HKCU</c> 的卸载表与关联表，一处都不需要管理员。<see cref="AllUsers"/> 走的是机器级那一套
/// （<c>Program Files</c> + <c>%ProgramData%</c> 的开始菜单 + <c>HKLM</c>），
/// 代价是必须提权一次 —— 两个作用域<strong>不能混用</strong>：机器级的安装一处都不写
/// 当前用户那半，否则"给所有人装了"却留下只有安装者看得见的东西。</para>
/// </summary>
public enum InstallScope
{
    CurrentUser,
    AllUsers,
}

/// <summary>一次"正常安装"要落下的每一件东西，以及它们落在哪里。</summary>
/// <param name="InstallDirectory">程序文件所在目录（默认是每个用户自己的，不需要管理员）。</param>
/// <param name="ExecutablePath">安装后的 exe 全路径。文件关联、快捷方式、卸载表都指向它。</param>
/// <param name="StartMenuShortcut">开始菜单那个快捷方式。</param>
/// <param name="StartMenuUninstallShortcut">开始菜单里那一个「卸载 Andzip」。名字来自语言包，
/// 所以卸载时要按注册表里记下的那个路径收回（见 <c>InstallationService</c>）。</param>
/// <param name="DesktopShortcut">桌面快捷方式；为 null 表示这台机器的桌面目录取不到，跳过。</param>
/// <param name="UninstallKey">注册表「应用和功能」那一条的键名。两个作用域下的<strong>相对路径是同一个</strong>
/// （<c>Software\Microsoft\Windows\CurrentVersion\Uninstall\Andzip</c>），差别只在挂在哪个根上 ——
/// 根由 <see cref="Scope"/> 决定。</param>
/// <param name="Scope">装给谁。默认当前用户。</param>
public sealed record InstallPlan(
    string InstallDirectory,
    string ExecutablePath,
    string StartMenuShortcut,
    string StartMenuUninstallShortcut,
    string? DesktopShortcut,
    string UninstallKey,
    InstallScope Scope = InstallScope.CurrentUser)
{
    /// <summary>卸载时要按同一个名字找回自己写过的 ProgId（<c>Andzip.*</c>）。</summary>
    public const string ProgIdPrefix = "Andzip.";

    /// <summary>安装目录里那个能双击的卸载入口（跟着程序文件走，所以由目录算出来）。</summary>
    public string UninstallScript => Path.Combine(InstallDirectory, InstallLayout.UninstallScriptName);
}

/// <summary>
/// 安装路径的计算。<strong>纯字符串</strong>：目录从外面传进来，
/// 所以"装到哪、快捷方式叫什么、卸载表写在哪"这些事实能在任何机器上被测试钉住，
/// 不需要真的装一次。
/// <para>为什么默认装 <c>%LOCALAPPDATA%\Programs</c>：那是唯一一处不需要管理员的常规位置，
/// 而 Andzip 的文件关联、右键动词、设置文件在默认作用域下都按"当前用户"设计。
/// 用户级目录同时避开了 UAC 虚拟化那一堆坑。</para>
/// <para>向导里那个位置可以改（<paramref name="installDirectory"/>），但在当前用户作用域下
/// <strong>只有程序文件跟着走</strong>：快捷方式与卸载表仍在用户目录下 —— 那两处本来就不需要管理员，
/// 把它们也搬到用户选的地方只会让"装到 D 盘"变成"要管理员"。</para>
/// <para>机器级那一份在 <see cref="PlanForAllUsers"/>。</para>
/// </summary>
public static class InstallLayout
{
    public const string AppFolder = "Andzip";
    public const string ExeName = "Andzip.exe";

    /// <summary>安装器那一份 exe 的名字前缀：同一个二进制，换个名字跑就是安装界面。</summary>
    public const string InstallerFileNamePrefix = "andzip-setup";

    /// <summary>
    /// 安装目录里那个能双击的卸载入口。
    /// <para>为什么是 .bat 而不是 <c>uninstall.exe</c>：真正的卸载逻辑在 <c>Andzip.exe /uninstall</c> 里
    /// （它知道两种作用域、快捷方式、注册表与"删掉正在运行的自己"那件事）。
    /// 一个真的 <c>uninstall.exe</c> 要么是那一份 20 MB 字节的第二份拷贝，
    /// 要么是一个只会启动上面那一条的壳 —— 壳用 90 字节的批处理就是同一个东西，
    /// 还不需要目标机器上有编译器。</para>
    /// </summary>
    public const string UninstallScriptName = "uninstall.bat";

    /// <summary>
    /// 批处理的内容。<see cref="ExeName"/> 拼进去，两边不会各自漂移。
    /// <para><strong>那一句 <c>cd</c> 不是排版</strong>：双击 .bat 时 cmd 的当前目录就是这个 .bat
    /// 所在的目录，而"进程把当前目录占着"在 Windows 上等于"这个目录删不掉" —— 卸载收尾那一步
    /// 收的是整个安装目录。先跳到 %TEMP% 再拉起卸载程序，目录上就没有人拿着句柄了。</para>
    /// </summary>
    public static string UninstallScriptText => string.Join("\r\n",
        "@echo off",
        "rem Andzip -- double-click to uninstall. The real uninstaller lives inside Andzip.exe;",
        "rem this file only exists so the install folder has an obvious way out.",
        "rem Leave the install folder as cmd's current directory: holding it open is what keeps",
        "rem the folder on disk after the uninstall finishes.",
        "cd /d \"%TEMP%\"",
        $"\"%~dp0{ExeName}\" /uninstall",
        string.Empty);

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

        return Shape(
            Directory(installDirectory, () => Path.Combine(localAppData, "Programs", AppFolder), nameof(installDirectory)),
            Path.Combine(roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs"),
            desktop,
            InstallScope.CurrentUser,
            uninstallShortcutName);
    }

    /// <summary>
    /// 机器级那一份：<c>%ProgramFiles%\Andzip</c>、<c>%ProgramData%</c> 下那个<strong>所有用户都看得见</strong>
    /// 的开始菜单与公共桌面，卸载表写 <c>HKLM</c>。
    /// <para>与 <see cref="Plan"/> 的关键区别是<strong>一处都不落在个人目录下</strong>：提权之后
    /// 那个进程的"当前用户"可能已经不是双击安装的人（用别的账户过 UAC 时就是），
    /// 混着写会得到"给所有人装了，但只有安装者的开始菜单里有"。</para>
    /// </summary>
    /// <param name="programFiles">通常是 <c>%ProgramFiles%</c>（64 位进程拿到的就是 64 位那个）。</param>
    /// <param name="commonPrograms">公共开始菜单的 Programs 目录，
    /// 通常是 <c>%ProgramData%\Microsoft\Windows\Start Menu\Programs</c>。</param>
    /// <param name="commonDesktop">公共桌面（<c>%PUBLIC%\Desktop</c>）；取不到传 null 就跳过。</param>
    public static InstallPlan PlanForAllUsers(
        string programFiles,
        string commonPrograms,
        string? commonDesktop,
        string? installDirectory = null,
        string? uninstallShortcutName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonPrograms);

        return Shape(
            Directory(installDirectory, () => Path.Combine(programFiles, AppFolder), nameof(installDirectory)),
            commonPrograms,
            commonDesktop,
            InstallScope.AllUsers,
            uninstallShortcutName);
    }

    private static string Directory(string? chosen, Func<string> fallback, string parameter)
        => string.IsNullOrWhiteSpace(chosen)
            ? fallback()
            // 用户填的路径先归一化：带尾斜杠、写成相对路径、混着 / 与 \ 这几种写法
            // 都会一路传到注册表的 InstallLocation 里，卸载时按它找回自己就对不上。
            : InstallTarget.Normalize(chosen)
                ?? throw new ArgumentException($"这个路径没法归一化：{chosen}", parameter);

    private static InstallPlan Shape(
        string directory,
        string programs,
        string? desktop,
        InstallScope scope,
        string? uninstallShortcutName)
        => new(
            directory,
            Path.Combine(directory, ExeName),
            Path.Combine(programs, AppFolder + ".lnk"),
            Path.Combine(programs, ShortcutFile(uninstallShortcutName ?? $"Uninstall {AppFolder}")),
            desktop is null ? null : Path.Combine(desktop.TrimEnd(Path.DirectorySeparatorChar), AppFolder + ".lnk"),
            UninstallKey,
            scope);

    /// <summary>
    /// 卸载时该收哪一处：<strong>先看"运行中的这一份"装在哪个位置</strong>，再退而看两处卸载表各记着什么。
    /// <para>为什么要这么绕：机器级与当前用户级可以<strong>同时存在</strong>（先给自己装、后来又叫管理员
    /// 给所有人装一次），而卸载程序是被 <c>UninstallString</c> 从某个具体 exe 拉起来的 ——
    /// 那个 exe 在哪，卸的就是哪一份。按"哪个键先存在"来判会卸错：HKCU 那条还在的时候卸机器级，
    /// 用户看到的正是"我点了卸载，Program Files 里那份却没动"。</para>
    /// <para>两处都对不上（用户自己把目录搬走 / 只留了注册表）时退回卸载表记着的那一处，
    /// 两处都记着就退机器级 —— 那一处留着更碍事。</para>
    /// </summary>
    public static InstallScope ScopeFor(string? runningDirectory, string? machineLocation, string? userLocation)
    {
        var here = InstallTarget.Normalize(runningDirectory);

        if (here is not null)
        {
            if (SameFolder(here, machineLocation))
            {
                return InstallScope.AllUsers;
            }

            if (SameFolder(here, userLocation))
            {
                return InstallScope.CurrentUser;
            }
        }

        return machineLocation is { Length: > 0 } ? InstallScope.AllUsers : InstallScope.CurrentUser;

        static bool SameFolder(string a, string? b)
            => b is { Length: > 0 }
            && string.Equals(InstallTarget.Normalize(b), a, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// 另一个作用域：装之前要先去看的那一处。
    /// <para>存在的理由是"一台机器上只该有一份 Andzip"这条规矩 —— 两份同时在的代价不是磁盘上
    /// 多 20 MB，而是桌面上两个都叫 Andzip 的图标、卸载表里两条都叫 Andzip 的项，
    /// 以及两套文件关联互相盖（HKCU 压过 HKLM，于是"给所有人装"的那一份的关联永远不生效）。</para>
    /// </summary>
    public static InstallScope Opposite(InstallScope scope)
        => scope == InstallScope.AllUsers ? InstallScope.CurrentUser : InstallScope.AllUsers;
}

/// <summary>
/// 这台机器上已经装着的那一份：在哪个作用域名下、装在哪个目录、卸载表记的是哪个版本。
/// </summary>
/// <param name="Version">卸载表里的 <c>DisplayVersion</c>；没记时为 null。
/// 界面上报的是"另一份是 2.8.0，在 C:\Program Files\Andzip" ——
/// 版本只是让人认出来是哪一次装的，判据是目录。</param>
public sealed record InstalledCopy(InstallScope Scope, string Directory, string? Version);

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
