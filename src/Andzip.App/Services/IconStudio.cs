using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Iconography;
using Andzip.Core.Installation;
using Andzip.Core.Settings;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 把用户挑的那套配色变成"这台机器上的图标"：落盘的 .ico / .png、快捷方式、
/// 关联表的 <c>DefaultIcon</c>、正在跑着的这几扇窗口。
/// <para><strong>为什么非要落一个文件</strong>：快捷方式与注册表只能指一个路径，
/// 指 exe 就等于指出厂那张 —— PE 的资源段在运行时改不了。所以自定义图标的唯一形态是
/// "<c>%APPDATA%\Andzip\Andzip.ico</c>，各处去指它"。</para>
/// <para><strong>为什么每一步都要回报做了什么</strong>：绿色版没有快捷方式，机器级安装的
/// 公共桌面可能写不进去，没关联过时注册表里根本没有 ProgId。这些情况下"设好了！"是句谎话，
/// 而用户只会看到桌面没变，然后认为这个功能是假的（与 1.8.4 那句"桌面快捷方式已创建"同一课）。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class IconStudio
{
    public const string IcoFileName = "Andzip.ico";
    public const string PngFileName = "Andzip.png";

    /// <summary>图标文件放哪儿。测试注入临时目录，真机是设置文件旁边那一格。</summary>
    public string Directory { get; }

    public string IcoPath => System.IO.Path.Combine(Directory, IcoFileName);

    public string PngPath => System.IO.Path.Combine(Directory, PngFileName);

    /// <summary>
    /// <paramref name="progIdPrefix"/> 是注册表那一趟的作用域，默认就是我们 owns 的那一个
    /// （<c>Andzip.</c>）。测试必须传自己那一个探针前缀 —— 这台开发机上真留着七条
    /// <c>Andzip.*\DefaultIcon</c>，测试要是照着默认值跑，就会把用户实际的格式图标改到临时目录里，
    /// 而「恢复默认」那一步更糟：它写回去的是 <c>Environment.ProcessPath</c>，
    /// 在测试里那是测试主机。<strong>这种副作用只有"注入作用域"能挡</strong>，
    /// 光靠"测试记得别点那条路"迟早会漏。
    /// </summary>
    /// <param name="ownExePath">「恢复默认」时写回 <c>DefaultIcon</c> 的那一个 exe。</param>
    public IconStudio(
        string? directory = null,
        string progIdPrefix = InstallPlan.ProgIdPrefix,
        string? ownExePath = null)
    {
        Directory = directory ?? System.IO.Path.GetDirectoryName(AppSettings.DefaultFilePath)!;
        ProgIdPrefix = progIdPrefix;
        OwnExePath = ownExePath ?? Environment.ProcessPath ?? string.Empty;
    }

    private string ProgIdPrefix { get; }

    private string OwnExePath { get; }

    /// <summary>
    /// 一次"设为桌面图标"或"恢复默认"之后，实际改动了哪几处。
    /// <para>每个数字都可能为 0 —— 界面必须照着这些数字说话。</para>
    /// </summary>
    public sealed record Outcome(
        bool FilesWritten,
        int ShortcutsUpdated,
        bool ShortcutsBlocked,
        int ProgIdsUpdated,
        string? Failure);

    /// <summary>生成 .ico 与 .png，换掉窗口图标，并把快捷方式与关联表指过来。</summary>
    public Outcome Apply(IconPreferences preferences, IEnumerable<string> shortcuts)
    {
        byte[] ico;
        byte[] png;

        try
        {
            ico = IconPainter.Ico(preferences.Glyph, preferences.Palette);
            png = IconPainter.Png(preferences.Glyph, preferences.Palette, 512);
        }
        catch (Exception ex)
        {
            return new Outcome(false, 0, false, 0, ex.Message);
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(IcoPath, ico);
            File.WriteAllBytes(PngPath, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Outcome(false, 0, false, 0, ex.Message);
        }

        if (!Assets.UseCustomIcon(IcoPath))
        {
            return new Outcome(true, 0, false, 0, "TEXT_ICON_APPLY_FAILED");
        }

        var (updated, blocked) = RepointShortcuts(shortcuts, IcoPath);
        var progIds = RepointRegisteredIcons(IcoPath);
        NotifyShell();

        return new Outcome(true, updated, blocked, progIds, null);
    }

    /// <summary>
    /// 退回内嵌那一张：删掉生成的文件，快捷方式与关联表指回 exe。
    /// <para>这一路也要能<strong>报告失败</strong>：只有「设为」那条说得了败、这条永远说成功，
    /// 界面上就会出现"已恢复出厂图标"而桌面上还是用户自己挑的那一张 ——
    /// 一句写死的回话，会在它描述的那件事第一次出错的那天变成假话。</para>
    /// </summary>
    public Outcome Restore(IEnumerable<string> shortcuts)
    {
        Assets.ClearCustomIcon();

        try
        {
            foreach (var path in new[] { IcoPath, PngPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            var (updated, blocked) = RepointShortcuts(shortcuts, iconPath: null);
            var progIds = string.IsNullOrEmpty(OwnExePath) ? 0 : RepointRegisteredIcons($"{OwnExePath},0");
            NotifyShell();

            return new Outcome(false, updated, blocked, progIds, null);
        }
        catch (Exception ex)
        {
            return new Outcome(false, 0, false, 0, ex.Message);
        }
    }

    /// <summary>
    /// 启动时把上次设过的那张接回来（文件不在就当作没设过 —— 换机器、清理过目录都是这个形状）。
    /// </summary>
    public bool RestoreFromSettings(IconPreferences preferences)
        => preferences.Applied && Assets.UseCustomIcon(IcoPath);

    /// <summary>
    /// 换快捷方式的图标字段。<strong>iconPath 为 null 就是"跟目标文件走"</strong>，也就是出厂那张。
    /// </summary>
    private static (int Updated, bool Blocked) RepointShortcuts(IEnumerable<string> shortcuts, string? iconPath)
    {
        var updated = 0;
        var blocked = false;

        foreach (var link in shortcuts)
        {
            if (!File.Exists(link))
            {
                continue;
            }

            try
            {
                ShellLink.SetIcon(link, iconPath);
                updated++;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
            {
                // 公共桌面上那一个（机器级安装）在普通权限下写不进去 —— 那是这台软件唯一
                // 不该自己动手的地方，不提权，照实说。
                blocked = true;
            }
        }

        return (updated, blocked);
    }

    /// <summary>
    /// 关联表里那些 <c>Andzip.*\DefaultIcon</c> 跟着换 —— 用户挑了配色，结果资源管理器里
    /// 那些 .zip 还穿着旧衣服，会读成"这个设置只管一半"。
    /// <para>只动<strong>已经存在</strong>的那几项：没登记过的 ProgId 不该因为换图标这件事凭空冒出来
    /// （那等于用户没勾过的东西被我们顺手做了）。</para>
    /// </summary>
    private int RepointRegisteredIcons(string value)
    {
        var touched = 0;

        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            if (classes is null)
            {
                return 0;
            }

            foreach (var name in classes.GetSubKeyNames())
            {
                if (!name.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var icon = classes.OpenSubKey($@"{name}\DefaultIcon", writable: true);
                if (icon is null)
                {
                    continue;
                }

                icon.SetValue(string.Empty, value);
                touched++;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return touched;
        }

        return touched;
    }

    private const int ShcneAssocChanged = unchecked((int)0x08000000);

    /// <summary>
    /// 逼 shell 重读图标。<strong>不调这一句，用户点了"设为桌面图标"会看到桌面没变</strong> ——
    /// 图标缓存是按文件路径记的，路径没变、内容变了，它不会自己去看。
    /// </summary>
    private static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>这台机器上存在的 Andzip 快捷方式（桌面 + 开始菜单 + 卸载那一项）。</summary>
    public static IEnumerable<string> ExistingShortcuts(InstallationService service)
    {
        var plans = new[] { service.CurrentPlan(), service.RecordedPlan() };

        return plans
            .SelectMany(p => new[] { p.DesktopShortcut, p.StartMenuShortcut, p.StartMenuUninstallShortcut })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
