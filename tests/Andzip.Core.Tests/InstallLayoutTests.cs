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

    // ---------------------------------------- 机器级那一份：装给所有用户（2.1.0）

    /// <summary>
    /// 机器级那一份<strong>一处都不落在个人目录下</strong>。
    /// <para>提权之后那个进程的"当前用户"可能已经不是双击安装的人（用别的账户过 UAC 时就是），
    /// 混着写会得到"给所有人装了，却只有安装者看得见"。</para>
    /// </summary>
    [Fact]
    public void TheAllUsersPlanPutsEverythingUnderTheMachineFolders()
    {
        var plan = InstallLayout.PlanForAllUsers(
            programFiles: @"C:\Program Files",
            commonPrograms: @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
            commonDesktop: @"C:\Users\Public\Desktop");

        Assert.Equal(InstallScope.AllUsers, plan.Scope);
        Assert.Equal(Path.Combine(@"C:\Program Files", "Andzip"), plan.InstallDirectory);
        Assert.Equal(Path.Combine(@"C:\Program Files", "Andzip", "Andzip.exe"), plan.ExecutablePath);
        Assert.Equal(
            Path.Combine(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs", "Andzip.lnk"),
            plan.StartMenuShortcut);
        Assert.Equal(
            Path.Combine(@"C:\Users\Public\Desktop", "Andzip.lnk"),
            plan.DesktopShortcut);

        // 两个作用域下的相对键路径是同一个，只有根不同 —— 少维护一套键名，也少一处写错的可能
        Assert.Equal(InstallLayout.UninstallKey, plan.UninstallKey);
        Assert.DoesNotContain(@"\Users\", plan.StartMenuShortcut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAllUsersPlanStillHonoursAChosenDirectory()
    {
        var plan = InstallLayout.PlanForAllUsers(
            @"C:\Program Files", @"C:\ProgramData\Start Menu\Programs", null,
            installDirectory: @"D:\Tools\Andzip\\");

        Assert.Equal(@"D:\Tools\Andzip", plan.InstallDirectory);
        Assert.Equal(InstallScope.AllUsers, plan.Scope);

        // 公共桌面取不到时跳过那一个，其余照常 —— 与用户级同一套规矩
        Assert.Null(plan.DesktopShortcut);
        Assert.NotNull(plan.StartMenuShortcut);
    }

    /// <summary>不传作用域时仍是"仅当前用户"：那一条路不需要管理员，是默认也是推荐。</summary>
    [Fact]
    public void TheOrdinaryPlanIsMarkedAsCurrentUserScoped()
    {
        var plan = InstallLayout.Plan(@"X:\Local", @"X:\Roaming", null);

        Assert.Equal(InstallScope.CurrentUser, plan.Scope);
    }

    /// <summary>
    /// 卸载该收哪一处：<strong>运行中的这个 exe 在哪，卸的就是哪一份</strong>。
    /// <para>两种作用域可以同时在（先给自己装、后来又给所有人装一次）。按"哪个键先存在"来判会卸错：
    /// 从 <c>Program Files</c> 那份点卸载，却因为 HKCU 那条也在就只收了用户级的东西 ——
    /// 用户看到的就是"我点了卸载，Program Files 里那份没动"。</para>
    /// </summary>
    [Theory]
    // 运行位置对得上机器级：卸机器级（HKCU 那条也在）
    [InlineData(@"C:\Program Files\Andzip\Andzip.exe", @"C:\Program Files\Andzip", @"C:\Users\frank\AppData\Local\Programs\Andzip", InstallScope.AllUsers)]
    // 运行位置对得上用户级：卸用户级（HKLM 那条也在）
    [InlineData(@"C:\Users\frank\AppData\Local\Programs\Andzip\Andzip.exe", @"C:\Program Files\Andzip", @"C:\Users\frank\AppData\Local\Programs\Andzip", InstallScope.CurrentUser)]
    // 路径写法不一致（尾斜杠、大小写）也要认得出来是同一处
    [InlineData(@"c:\program files\andzip\Andzip.exe", @"C:\Program Files\Andzip\", @"C:\Users\frank\x", InstallScope.AllUsers)]
    // 对不上任何一处（比如从下载目录里那个安装器点卸载）：退到记着的那一处，两处都记着退机器级
    [InlineData(@"C:\Users\frank\Downloads\setup.exe", @"C:\Program Files\Andzip", @"C:\Users\frank\x", InstallScope.AllUsers)]
    [InlineData(@"C:\Users\frank\Downloads\setup.exe", null, @"C:\Users\frank\x", InstallScope.CurrentUser)]
    [InlineData(null, @"C:\Program Files\Andzip", null, InstallScope.AllUsers)]
    [InlineData(null, null, null, InstallScope.CurrentUser)]
    public void UninstallFollowsTheCopyThatIsActuallyRunning(
        string? running, string? machine, string? user, InstallScope expected)
    {
        var directory = running is null ? null : Path.GetDirectoryName(running);

        Assert.Equal(expected, InstallLayout.ScopeFor(directory, machine, user));
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
