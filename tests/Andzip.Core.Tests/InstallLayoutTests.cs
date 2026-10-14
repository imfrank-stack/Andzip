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
        Assert.Throws<ArgumentException>(() => InstallLayout.Plan(@"X:\Local", @"X:\Roaming", null, @"D:\非法<名字>"));
    }

    // ---------------------------------------------- 向导里那一页：位置可以改、空间要报得准

    /// <summary>
    /// 改了安装位置，<strong>只有程序文件那两处跟着走</strong>。
    /// <para>快捷方式与卸载表仍在当前用户目录下 —— 那两处本来就不需要管理员，
    /// 把它们也搬到用户选的地方只会让"装到 D 盘"变成"要管理员"。</para>
    /// </summary>
    [Fact]
    public void AChosenInstallDirectoryMovesOnlyTheProgramFiles()
    {
        var plan = InstallLayout.Plan(
            localAppData: @"C:\Users\frank\AppData\Local",
            roamingAppData: @"C:\Users\frank\AppData\Roaming",
            desktop: @"D:\Users\frank\Desktop",
            installDirectory: @"D:\Tools\Andzip");

        Assert.Equal(@"D:\Tools\Andzip", plan.InstallDirectory);
        Assert.Equal(Path.Combine(@"D:\Tools\Andzip", "Andzip.exe"), plan.ExecutablePath);

        Assert.EndsWith(
            Path.Combine("Start Menu", "Programs", "Andzip.lnk"),
            plan.StartMenuShortcut,
            StringComparison.Ordinal);
        Assert.Equal(Path.Combine(@"D:\Users\frank\Desktop", "Andzip.lnk"), plan.DesktopShortcut);
        Assert.Equal(InstallLayout.UninstallKey, plan.UninstallKey);
    }

    /// <summary>尾斜杠、相对写法、混着 <c>/</c> 与 <c>\</c> —— 一路传到注册表里就得是同一个样子。</summary>
    [Fact]
    public void AChosenDirectoryIsNormalisedBeforeItReachesTheRegistry()
    {
        var plan = InstallLayout.Plan(
            @"C:\Users\frank\AppData\Local", @"C:\Users\frank\AppData\Roaming", null,
            installDirectory: @"D:\Tools\Andzip\");

        Assert.Equal(@"D:\Tools\Andzip", plan.InstallDirectory);
        Assert.False(plan.InstallDirectory.EndsWith('\\'), "留着尾斜杠会一路拼成 D:\\Tools\\Andzip\\Andzip.exe");
    }

    /// <summary>
    /// 卸载快捷方式的名字来自语言包，而翻译里出现 <c>:</c> 或 <c>/</c> 是可能的 ——
    /// 那种文件名保存不下来，症状恰好是"开始菜单里没有卸载那一项"且一声不响。
    /// </summary>
    [Fact]
    public void AnUninstallShortcutNameFromTheLanguageFileSurvivesIllegalCharacters()
    {
        var plan = InstallLayout.Plan(
            @"X:\Local", @"X:\Roaming", null,
            uninstallShortcutName: @"卸载/Andzip: 卸载");

        var name = Path.GetFileName(plan.StartMenuUninstallShortcut);

        Assert.EndsWith(".lnk", name, StringComparison.Ordinal);
        Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
            $"快捷方式的名字里有文件系统不接受的字符：{name}");
        Assert.StartsWith(Path.Combine(@"X:\Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.GetDirectoryName(plan.StartMenuUninstallShortcut),
            StringComparison.Ordinal);
    }

    /// <summary>所需空间 = 源文件实际字节，不是估的。</summary>
    [Fact]
    public void RequiredSpaceIsExactlyTheSourceFileSize()
    {
        var file = Path.Combine(Path.GetTempPath(), $"andzip-space-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        try
        {
            Assert.Equal(4096, InstallTarget.RequiredBytes(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AvailableSpaceSaysUnknownRatherThanZeroForAPathThatIsNotThere()
    {
        // 一个假的"剩余 0"会让人以为盘满了，所以读不到必须是 null，由界面说"无法确定"
        Assert.Null(InstallTarget.AvailableBytes("   "));
        Assert.Null(InstallTarget.AvailableBytes(null));
        Assert.Null(InstallTarget.AvailableBytes(@"X:\没有这个盘\Andzip"));

        // 真实存在的那个卷必须报得出数（这台机器上 C: 总在）
        Assert.NotNull(InstallTarget.AvailableBytes(Path.GetTempPath()));
        Assert.True(InstallTarget.AvailableBytes(Path.GetTempPath()) > 0);
    }

    [Fact]
    public void NormaliseKeepsAWholePathAndRejectsGarbage()
    {
        Assert.Equal(@"D:\Tools\Andzip", InstallTarget.Normalize(@"  D:\Tools\Andzip\ "));
        Assert.Equal(@"D:\Tools\Andzip", InstallTarget.Normalize(@"D:/Tools/Andzip"));
        Assert.Null(InstallTarget.Normalize(""));
        Assert.Null(InstallTarget.Normalize(@"D:\不合法的<名字>"));
    }

    /// <summary>
    /// 可写性探测<strong>不能把目标目录建出来</strong>：用户在向导里一路改路径，
    /// 每改一次就建一层空目录、取消安装之后盘上一堆空壳。
    /// </summary>
    [Fact]
    public void TheWriteProbeLeavesNoEmptyFolderBehind()
    {
        var root = Directory.CreateTempSubdirectory("andzip-probe-").FullName;

        try
        {
            var deep = Path.Combine(root, "新建的", "再新建的");

            Assert.True(InstallTarget.IsWritable(deep));
            Assert.False(Directory.Exists(deep), "探一下就把目录建出来了，取消之后盘上会留空壳。");

            // 祖先不存在（X: 这台机器没有）时必须报不可写，而不是抛
            Assert.False(InstallTarget.IsWritable(@"X:\Andzip"));
            Assert.False(InstallTarget.IsWritable(""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
