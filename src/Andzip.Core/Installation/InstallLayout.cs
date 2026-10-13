namespace Andzip.Core.Installation;

/// <summary>一次"正常安装"要落下的每一件东西，以及它们落在哪里。</summary>
/// <param name="InstallDirectory">程序文件所在目录（每个用户自己的，不需要管理员）。</param>
/// <param name="ExecutablePath">安装后的 exe 全路径。文件关联、快捷方式、卸载表都指向它。</param>
/// <param name="StartMenuShortcut">开始菜单那个快捷方式。</param>
/// <param name="DesktopShortcut">桌面快捷方式；为 null 表示这台机器的桌面目录取不到，跳过。</param>
/// <param name="UninstallKey">注册表「应用和功能」那一条的键名（HKCU 下，不需要管理员）。</param>
public sealed record InstallPlan(
    string InstallDirectory,
    string ExecutablePath,
    string StartMenuShortcut,
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
/// <para>为什么装 <c>%LOCALAPPDATA%\Programs</c>：<c>Program Files</c> 要管理员，
/// 而 Andzip 整条产品线（文件关联、右键动词、设置文件）都是按"当前用户"设计的，
/// 没有一处需要写机器级配置。用户级目录同时避开了 UAC 虚拟化那一堆坑。</para>
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
    public static InstallPlan Plan(string localAppData, string roamingAppData, string? desktop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);

        var directory = Path.Combine(localAppData, "Programs", AppFolder);

        return new InstallPlan(
            directory,
            Path.Combine(directory, ExeName),
            Path.Combine(roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", AppFolder + ".lnk"),
            desktop is null ? null : Path.Combine(desktop, AppFolder + ".lnk"),
            UninstallKey);
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
