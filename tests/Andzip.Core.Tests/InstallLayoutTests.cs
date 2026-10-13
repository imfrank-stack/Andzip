using Andzip.Core.Installation;

namespace Andzip.Core.Tests;

/// <summary>
/// 「正常安装」的落点计算。这些是字符串层面的事实，所以不需要真的装一次就能钉住 ——
/// 而它们一旦错了，症状是"桌面上没有图标"或"控制面板里卸载不掉"，都很难查。
/// </summary>
public class InstallLayoutTests
{
    [Fact]
    public void PlanPutsEverythingUnderTheCurrentUserFolders()
    {
        var plan = InstallLayout.Plan(
            localAppData: @"C:\Users\frank\AppData\Local",
            roamingAppData: @"C:\Users\frank\AppData\Roaming",
            desktop: @"C:\Users\frank\OneDrive\Desktop");

        Assert.Equal(
            Path.Combine(@"C:\Users\frank\AppData\Local", "Programs", "Andzip"),
            plan.InstallDirectory);
        Assert.Equal(
            Path.Combine(plan.InstallDirectory, "Andzip.exe"),
            plan.ExecutablePath);

        // 开始菜单在 APPDATA（漫游）而不是 LOCALAPPDATA：换机器登录时这一项要跟着走
        Assert.EndsWith(
            Path.Combine("Microsoft", "Windows", "Start Menu", "Programs", "Andzip.lnk"),
            plan.StartMenuShortcut);
        Assert.Equal(
            Path.Combine(@"C:\Users\frank\OneDrive\Desktop", "Andzip.lnk"),
            plan.DesktopShortcut);

        // 卸载表在 HKCU 下：整条安装路径一处都不需要管理员
        Assert.StartsWith("Software\\", plan.UninstallKey, StringComparison.Ordinal);
        Assert.DoesNotContain("HKEY", plan.UninstallKey, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDesktopFolderSkipsTheDesktopShortcutInsteadOfFailing()
    {
        var plan = InstallLayout.Plan(@"X:\Local", @"X:\Roaming", desktop: null);

        Assert.Null(plan.DesktopShortcut);

        // 桌面取不到是可能的（未知文件夹接口失败、精简过的系统），但开始菜单与程序文件
        // 必须照常，不能因为少一个快捷方式就整个装不了
        Assert.NotNull(plan.StartMenuShortcut);
        Assert.NotNull(plan.ExecutablePath);
    }

    [Fact]
    public void TheInstallerIsRecognisedByItsOwnFileName()
    {
        Assert.True(InstallLayout.IsInstallerFile(@"D:\Downloads\Andzip-Setup-1.8.0-win-x64.exe"));
        Assert.True(InstallLayout.IsInstallerFile(@"D:\a\andzip-setup.exe"));

        // 装进电脑里的那一份必须开主窗口：文件名判据一旦放宽，双击 Andzip.exe 会先弹安装界面
        Assert.False(InstallLayout.IsInstallerFile(@"C:\Users\frank\AppData\Local\Programs\Andzip\Andzip.exe"));
        Assert.False(InstallLayout.IsInstallerFile(@"D:\Downloads\Andzip-1.7.0-win-x64.exe"));
    }

    [Fact]
    public void EmptyFoldersAreRejectedRatherThanProducingARelativePath()
    {
        // 传空串会算出 "Andzip\Andzip.exe" 这种相对路径，装到当前工作目录去
        Assert.Throws<ArgumentException>(() => InstallLayout.Plan("", @"X:\Roaming", null));
        Assert.Throws<ArgumentException>(() => InstallLayout.Plan(@"X:\Local", " ", null));
    }
}
