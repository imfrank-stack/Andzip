using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Installation;
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
            .Single(b => Equals(b.Content?.ToString(), lang.Label("TEXT_BTN_UNINSTALL")));
        var installedOnThisMachine = File.Exists(new InstallationService(lang).CurrentPlan().ExecutablePath);
        Assert.Equal(installedOnThisMachine, uninstall.IsVisible);
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
