using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Win32;

namespace Andzip.App.UiTests;

/// <summary>
/// 「正常安装」这条路径的往返实测：真的装一次，看桌面/开始菜单/卸载表三处都落了东西，
/// 再真的卸一次，看它们都收回去了。
/// <para>为什么不用假的文件系统：快捷方式是 shell 的 <c>.lnk</c> 二进制格式，
/// 卸载表是注册表 —— 这两样"代码写了"与"系统里真有"是两件事，
/// 而它们的失败方式恰好都是"用户看不见任何报错，只是没有图标"。</para>
/// <para>落点全部指向临时目录，注册表写的是探针键；<c>associate</c> 与 <c>contextMenu</c>
/// 一律传 false —— 那两条会动开发机上的真实关联表，测试不该有那种副作用。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class InstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-install-").FullName;
    private readonly InstallPlan _plan;

    public InstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Desktop"));

        _plan = InstallLayout.Plan(
            Path.Combine(_root, "Local"),
            Path.Combine(_root, "Roaming"),
            Path.Combine(_root, "Desktop"))
            with
        {
            UninstallKey = @"Software\Andzip.Tests\InstallerProbe",
        };
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_plan.UninstallKey, throwOnMissingSubKey: false);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录留在 %TEMP% 里无所谓，不值得让一次测试因此变红
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void InstallPutsTheExeTheShortcutsAndTheUninstallEntryInPlace()
    {
        var outcome = Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        Assert.True(File.Exists(_plan.ExecutablePath), "程序文件没落到安装目录。");
        Assert.Equal(_plan.ExecutablePath, outcome.ExecutablePath);

        Assert.True(File.Exists(_plan.StartMenuShortcut), "开始菜单没有快捷方式。");
        Assert.True(File.Exists(_plan.DesktopShortcut), "桌面没有快捷方式。");

        using var key = Registry.CurrentUser.OpenSubKey(_plan.UninstallKey);
        Assert.NotNull(key);
        Assert.Equal("Andzip", key!.GetValue("DisplayName"));
        Assert.Contains("/uninstall", (string?)key.GetValue("UninstallString") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(_plan.ExecutablePath, key.GetValue("DisplayIcon"));
    }

    /// <summary>
    /// 桌面图标带透明通道这件事，能测的部分是"快捷方式确实指向那个 exe 的第 0 号图标"——
    /// 透明与否由 <c>assets/make_icon.py</c> 出的 ICO 保证（<see cref="AssetIconTests"/> 在那里钉 alpha）。
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void DesktopShortcutTakesItsIconFromTheInstalledExe()
    {
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var (path, index) = ShellLink.ReadIcon(_plan.DesktopShortcut!);

        Assert.Equal(0, index);
        Assert.True(path == _plan.ExecutablePath,
            $"快捷方式里的图标字段是「{path}」，期望「{_plan.ExecutablePath}」");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void UninstallTakesEverythingBackOut()
    {
        var service = Service();
        service.Install(_plan, SourceExe(), associate: false, contextMenu: false);

        Assert.True(service.Uninstall(_plan), "卸载报告说删干净了，但安装目录还在（或反过来）。");

        Assert.False(Directory.Exists(_plan.InstallDirectory), "安装目录还在。");
        Assert.False(File.Exists(_plan.StartMenuShortcut), "开始菜单的快捷方式还在。");
        Assert.False(File.Exists(_plan.DesktopShortcut!), "桌面的快捷方式还在。");
        Assert.True(Registry.CurrentUser.OpenSubKey(_plan.UninstallKey) is null, "卸载表里那一条还在。");
    }

    /// <summary>
    /// 没勾关联与右键菜单时，卸载表里那两条记号必须是 0。
    /// <para>这条看着小，实际是"卸载不该顺手删掉它没做过的事"的唯一凭据 ——
    /// 没有它，用户自己从「设置」里登记的关联会在卸载时被连根拔掉。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void InstallRecordsWhichExtrasItActuallyDid()
    {
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        using var key = Registry.CurrentUser.OpenSubKey(_plan.UninstallKey);
        Assert.Equal(0, key?.GetValue("AndzipAssociate"));
        Assert.Equal(0, key?.GetValue("AndzipContextMenu"));
    }

    /// <summary>
    /// 安装器界面上的按钮与两个开关都在，而且<strong>没装过就看不见卸载那颗</strong>。
    /// <para>这条不测装了什么 —— 上面那几条测的就是那个。它测的是"用户看得见哪些动作"，
    /// 尤其是那颗会删掉整个程序的红色按钮不该在没东西可删的时候还摆在那里。</para>
    /// </summary>
    [AvaloniaFact]
    public void InstallerWindowOffersInstallAndHidesUninstallWhenNothingIsInstalled()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang).Shown();

        var titles = VisualQuery.VisibleTexts(window);
        Assert.Contains(lang.Label("TEXT_BTN_INSTALL"), titles);
        Assert.Contains(lang.Label("TEXT_BTN_UNINSTALL"), titles);
        Assert.Contains(lang.Label("TEXT_BTN_CLOSE"), titles);
        Assert.Contains(lang.Label("TEXT_INSTALLER_ASSOCIATE"), titles);
        Assert.Contains(lang.Label("TEXT_INSTALLER_CONTEXT"), titles);

        var uninstall = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_BTN_UNINSTALL")));
        var installedOnThisMachine = File.Exists(new InstallationService(lang).CurrentPlan().ExecutablePath);
        Assert.Equal(installedOnThisMachine, uninstall.IsVisible);
    }

    /// <summary>
    /// 真的点一次「安装」。
    /// <para>1.8.1 装不上就是这个形状：安装跑在后台线程上，而后台线程里读的是
    /// <c>SettingToggle.IsChecked</c>（背后是 Avalonia 的 <c>ToggleButton</c>）——
    /// 跨线程碰控件属性直接抛 "Call from invalid thread"，界面上只剩这一行英文。
    /// 上面那几条都是绕开界面直接调 <c>InstallationService</c> 的，所以拦不住；
    /// 这一条走的是用户那条路：按按钮 → 等异步跑完 → 看界面上写了什么。</para>
    /// <para>两个开关在这里关掉：关联与右键菜单会动开发机的真实配置，
    /// 而这条要钉的是"按钮到落盘这条线通不通"，不是那两条注册表逻辑（上面已单独测）。</para>
    /// </summary>
    [AvaloniaFact]
    public void ClickingInstallActuallyInstallsAndReportsOnTheUi()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        var shown = ClickInstallAndWait(window, lang);

        Assert.True(shown.Contains(lang.Label("TEXT_INSTALLER_DONE"), StringComparison.Ordinal),
            $"点完安装，界面没有回话「{lang.Label("TEXT_INSTALLER_DONE")}」，实际是：{shown}");
        Assert.DoesNotContain("invalid thread", shown, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_plan.ExecutablePath), "界面说装好了，可安装目录里没有程序文件。");
        Assert.True(File.Exists(_plan.StartMenuShortcut), "界面说装好了，可开始菜单没有快捷方式。");
    }

    /// <summary>
    /// 取不到桌面目录时，界面上那句回话<strong>不能提桌面</strong>。
    /// <para>1.8.4 真机核对抓到的就是这一类：装完显示"桌面与开始菜单的快捷方式已创建"，
    /// 桌面上却什么也没有 —— 因为 <c>FOLDERID_Desktop</c> 的 GUID 凭记忆写错了，
    /// 桌面路径解析成 null，那一枚快捷方式被静默跳过，而回话是写死的。</para>
    /// </summary>
    [AvaloniaFact]
    public void UiStopsClaimingTheDesktopShortcutWhenThereIsNoDesktopFolder()
    {
        var lang = TestLang.Chinese();
        var noDesktop = InstallLayout.Plan(
            Path.Combine(_root, "Local"), Path.Combine(_root, "Roaming"), desktop: null)
            with { UninstallKey = _plan.UninstallKey };

        var window = new InstallerWindow(lang, noDesktop).Shown();
        var shown = ClickInstallAndWait(window, lang);

        Assert.True(File.Exists(noDesktop.StartMenuShortcut), "开始菜单那一个也没建出来，那就不是桌面目录的事了。");
        Assert.True(shown.Contains(lang.Label("TEXT_INSTALLER_DONE_NO_DESKTOP"), StringComparison.Ordinal),
            $"界面没有照实说「{lang.Label("TEXT_INSTALLER_DONE_NO_DESKTOP")}」，实际是：{shown}");
        Assert.DoesNotContain(lang.Label("TEXT_INSTALLER_DONE"), shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// 桌面目录必须真的解析得出来。<strong>这条量的是这台机器上的 shell，不是我们的字符串拼接</strong>：
    /// 上面那些测试都自己传临时目录，所以 <c>FOLDERID_Desktop</c> 写错时它们全绿。
    /// <para>对照用的是另一条独立的路（<c>SHGetFolderPath</c> / CSIDL，.NET 的
    /// <c>GetFolderPath</c> 走的就是它）：两个 API 都读 <c>User Shell Folders\Desktop</c>，
    /// 其中一个返回 null 或指到别处，就说明我们那个 GUID 不对。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void DesktopFolderResolvesToTheFolderTheShellItselfShows()
    {
        var desktop = InstallationService.DesktopFolder();

        Assert.NotNull(desktop);
        Assert.True(Directory.Exists(desktop), $"解析出来的桌面目录并不存在：{desktop}");
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.TrimEndingDirectorySeparator(desktop!));
    }

    /// <summary>按「安装」并等界面把话说完，返回那一刻界面上所有可见文字。</summary>
    private static string ClickInstallAndWait(InstallerWindow window, LangCatalog lang)
    {
        foreach (var row in VisualQuery.Of<SettingToggle>(window))
        {
            row.IsChecked = false;
        }

        return ClickAndWait(window, lang, Button(window, lang, "TEXT_BTN_INSTALL"), lang.Label("TEXT_INSTALLER_WORKING"));
    }

    /// <summary>
    /// 按一颗按钮并等界面把话说完（状态行不再写着"正在……"），返回那一刻的所有可见文字。
    /// </summary>
    private static string ClickAndWait(InstallerWindow window, LangCatalog lang, Button button, string busyText)
    {
        Clicker.Invoke(button);

        // 等的是<strong>界面自己把话说完</strong>，不是"目标文件出现了"：
        // File.Exists 在复制刚开始（文件被创建出来）就为真，那样等于在测一个半成品。
        var deadline = Environment.TickCount64 + 20_000;
        var shown = string.Empty;
        while (Environment.TickCount64 < deadline)
        {
            UiWait.Pump(50);
            shown = string.Join("|", VisualQuery.VisibleTexts(window));
            if (!shown.Contains(busyText, StringComparison.Ordinal))
            {
                break;
            }
        }

        return shown;
    }

    private static Button Button(InstallerWindow window, LangCatalog lang, string labelKey)
        => VisualQuery.Of<Button>(window).Single(b => Equals(VisualQuery.TextOf(b), lang.Label(labelKey)));

    /// <summary>
    /// 从界面上卸完，那一句"这台机器上已经装过"必须跟着改口。
    /// <para>真机截图里抓到的矛盾：红色卸载按钮按磁盘状态自己消失了，同一屏上那句
    /// "这台机器上已经装过 C:\…\Andzip.exe" 却还留着 —— 一件事的两个说法，一个跟着状态走一个不走。</para>
    /// </summary>
    [AvaloniaFact]
    public void UiStopsClaimingInstalledAfterUninstallingFromTheUi()
    {
        var lang = TestLang.Chinese();
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var window = new InstallerWindow(lang, _plan).Shown();
        var before = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.True(before.Contains(lang.Format("TEXT_INSTALLER_ALREADY", ("dir", _plan.ExecutablePath)), StringComparison.Ordinal),
            $"刚装好时界面上没写「装过」，那这条测试的前提就没了：{before}");

        var shown = ClickAndWait(window, lang,
            Button(window, lang, "TEXT_BTN_UNINSTALL"), lang.Label("TEXT_INSTALLER_REMOVING"));

        Assert.True(shown.Contains(lang.Label("TEXT_INSTALLER_REMOVED"), StringComparison.Ordinal),
            $"点完卸载，界面没有回话「{lang.Label("TEXT_INSTALLER_REMOVED")}」，实际是：{shown}");
        Assert.True(shown.Contains(lang.Label("TEXT_INSTALLER_NOT_YET"), StringComparison.Ordinal),
            $"东西已经删干净了，界面上却还写着装过：{shown}");
    }

    private InstallationService Service() => new(TestLang.Chinese());

    /// <summary>一个"够用像 exe"的源文件：安装器复制的是字节，不看内容。</summary>
    private string SourceExe()
    {
        var path = Path.Combine(_root, "Andzip-Setup-source.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);
        return path;
    }
}
