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
/// 「为所有用户安装」这一档（2.1.0）。
/// <para><strong>为什么这里没有一次真的写 <c>HKLM</c></strong>：这台机器上的账户不在
/// Administrators 组里（实测 <c>IsInRole(Administrator) == false</c>），真的机器级写入与真的 UAC
/// 弹窗在这里都跑不了 —— 弹出来的那个框也不是自动化该去按的东西。
/// 所以这一族把能钉的都钉住：作用域 → 注册表根的映射（含 64 位视图那条坑）、
/// 什么时候才需要提权、界面上问不问、被拒时停在哪、以及<strong>整条机器级往返</strong>
/// —— 后者靠把根换成"两个作用域都指 HKCU 的探针键"来跑，落点仍然是临时目录。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class InstallScopeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-scope-").FullName;
    private readonly InstallPlan _user;
    private readonly InstallPlan _machine;

    public InstallScopeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Desktop"));

        _user = InstallLayout.Plan(
            Path.Combine(_root, "Local"),
            Path.Combine(_root, "Roaming"),
            Path.Combine(_root, "Desktop")) with { UninstallKey = ProbeKey };

        // 机器级那一份：Program Files / 公共开始菜单 / 公共桌面都换成这同一个临时根下的替身，
        // 注册表根由测试注入（见下面的 BothUsers），于是"装给所有用户"这条路径可以整条真跑。
        _machine = InstallLayout.PlanForAllUsers(
            Path.Combine(_root, "Program Files"),
            Path.Combine(_root, "ProgramData", "Start Menu", "Programs"),
            Path.Combine(_root, "Public", "Desktop")) with { UninstallKey = ProbeKey };
    }

    private const string ProbeKey = @"Software\Andzip.Tests\ScopeProbe";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ProbeKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(OneCopyRoot, throwOnMissingSubKey: false);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>两个作用域都指 HKCU —— 真写 HKLM 在这台机器上做不到（账户不是管理员）。</summary>
    private static RegistryKey BothUsers(InstallScope _) => Registry.CurrentUser;

    // ------------------------------------------------- 根的选择与提权的判据

    /// <summary>
    /// 机器级必须显式走 <strong>64 位视图</strong>：32 位进程眼里 <c>HKLM\Software\Classes</c> 会被
    /// 重定向到 <c>WOW6432Node</c>，症状正是"注册表里明明写了、资源管理器里看不见"。
    /// 本项目出的是 x64 产物看着撞不上，但这条不该建立在"我们正好是 64 位"这种隐含前提上。
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheMachineScopeAsksForThe64BitRegistryView()
    {
        Assert.Equal(
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            RegistryScope.RootFor(InstallScope.AllUsers));
        Assert.Equal(
            (RegistryHive.CurrentUser, RegistryView.Default),
            RegistryScope.RootFor(InstallScope.CurrentUser));
    }

    /// <summary>
    /// 要不要提权，问的是"这个位置现在写不写得进去"，不是"这个账户是不是管理员"。
    /// <para>前者与安装真正会失败的条件是同一件事；后者还要引 <c>WindowsIdentity</c>
    /// （非 Windows 目标上没有），而且两者迟早给出不同答案。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnlyAMachineScopeThatCannotBeWrittenNeedsElevation()
    {
        Assert.False(Elevation.Needed(_user));

        // 机器级但那个位置写得动（测试里是临时目录）：不用打扰用户
        Assert.False(Elevation.Needed(_machine));

        var elsewhere = _machine with
        {
            InstallDirectory = @"X:\Programs\Andzip",
            ExecutablePath = @"X:\Programs\Andzip\Andzip.exe",
        };
        Assert.True(Elevation.Needed(elsewhere), "写不进去的位置必须走提权，否则进度条走到一半才失败。");

        // 反过来：当前用户那一份就算位置写得别扭，也**不该**把用户送去过一次 UAC ——
        // 那条路没有"提权就能写"的语义，报错就该报在位置那一页上。
        var blockedUserFolder = _user with
        {
            InstallDirectory = @"X:\Programs\Andzip",
            ExecutablePath = @"X:\Programs\Andzip\Andzip.exe",
        };
        Assert.False(Elevation.Needed(blockedUserFolder), "「仅为我自己」这一份什么时候都不该要管理员权限。");
    }

    // --------------------------------------------------------- 界面上的那一页

    /// <summary>欢迎之后先问"装给谁"，再问"装到哪"—— 位置那一页的默认值取决于前者。</summary>
    [AvaloniaFact]
    public void TheWizardAsksWhoToInstallForBeforeWhere()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _user).Shown();

        Click(window, lang, "TEXT_BTN_NEXT");
        var shown = string.Join("|", VisualQuery.VisibleTexts(window));

        Assert.Contains(lang.Label("TEXT_SETUP_SCOPE_TITLE"), shown);
        Assert.Contains(lang.Label("TEXT_SETUP_SCOPE_USER"), shown);
        Assert.Contains(lang.Label("TEXT_SETUP_SCOPE_ALL"), shown);
        Assert.Contains(lang.Label("TEXT_SETUP_SCOPE_NOTE"), shown);

        // 默认必须是"仅为我自己"：那一条不需要管理员，也是绝大多数人的答案
        Assert.True(Radios(window)[0].IsChecked, "默认应该选在「仅为我自己」上。");
        Assert.True(Radios(window)[1].IsChecked != true, "默认不该把用户直接送去过一次 UAC。");

        Click(window, lang, "TEXT_BTN_NEXT");
        Assert.Contains(lang.Label("TEXT_SETUP_LOCATION_TITLE"),
            string.Join("|", VisualQuery.VisibleTexts(window)));
    }

    /// <summary>
    /// 选了「所有用户」而那个位置写不进去 → <strong>先要提权</strong>，不问第二遍也不直接开始装。
    /// </summary>
    [AvaloniaFact]
    public void AWriteProtectedMachineScopeAsksForElevationBeforeGoingOn()
    {
        var lang = TestLang.Chinese();
        var asked = new List<InstallScope>();
        var window = new InstallerWindow(
            lang, BlockedMachine(), registryRoot: BothUsers,
            startsAsUninstaller: false, preselectedScope: null,
            elevate: scope => { asked.Add(scope); return false; }).Shown();

        Click(window, lang, "TEXT_BTN_NEXT");
        Radios(window)[1].IsChecked = true;
        Click(window, lang, "TEXT_BTN_NEXT");

        Assert.Single(asked);
        Assert.Equal(InstallScope.AllUsers, asked[0]);

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.True(shown.Contains(lang.Label("TEXT_SETUP_SCOPE_TITLE"), StringComparison.Ordinal),
            $"提权没成，就该停在范围那一页：{shown}");
        Assert.Contains(lang.Label("TEXT_SETUP_SCOPE_DENIED"), shown);
        Assert.False(File.Exists(BlockedMachine().ExecutablePath), "没提权就开始装了。");
    }

    /// <summary>
    /// 提权成功后<strong>这一份就该收窗退出</strong>：它没有权限，留着只会做半截安装，
    /// 而那个新的、有权限的实例会带着 <c>/all-users</c> 从位置那一页接着走。
    /// </summary>
    [AvaloniaFact]
    public void HandingOverToTheElevatedInstanceClosesThisOne()
    {
        var lang = TestLang.Chinese();
        var closed = false;
        var window = new InstallerWindow(
            lang, BlockedMachine(), registryRoot: BothUsers,
            startsAsUninstaller: false, preselectedScope: null, elevate: _ => true).Shown();
        window.Closed += (_, _) => closed = true;

        Click(window, lang, "TEXT_BTN_NEXT");
        Radios(window)[1].IsChecked = true;
        Click(window, lang, "TEXT_BTN_NEXT");

        Assert.True(closed, "提权交出去了，这一份却还开着 —— 两份同时在场，用户不知道哪个在装。");
    }

    /// <summary>提权之后的那一个新的实例：<strong>不再问第二遍装给谁</strong>，直接到位置那一页。</summary>
    [AvaloniaFact]
    public void TheElevatedInstancePicksUpOnTheLocationPage()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(
            lang, _machine, registryRoot: BothUsers,
            startsAsUninstaller: false, preselectedScope: InstallScope.AllUsers, elevate: null).Shown();

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.Contains(lang.Label("TEXT_SETUP_LOCATION_TITLE"), shown);
        Assert.DoesNotContain(lang.Label("TEXT_SETUP_SCOPE_TITLE"), shown);
        // 路径写在 TextBox 里，而 VisibleTexts 读的是"对用户写着什么"的控件 —— 输入框的正文
        // 不在那份名单上，所以这里直接量那颗框。
        Assert.Equal(_machine.InstallDirectory, VisualQuery.Of<TextBox>(window).Single().Text);
    }

    /// <summary>
    /// 机器级那一份<strong>收掉它也要先问一句</strong>（2.4.0）。
    /// <para>2.1.0 只给"装"那一路接了提权，"卸"那一路没有：从安装目录里双击
    /// <c>uninstall.bat</c> 进来是普通权限，于是跑到进度条上才失败，留下"点了卸载，那份却没动"。
    /// 这条把安装那一路的判据原样搬过来 —— 同一个位置，写与删要的是同一份权限。</para>
    /// </summary>
    [AvaloniaFact]
    public void AMachineCopyThatNeedsAdminAsksBeforeRemovingAnything()
    {
        var lang = TestLang.Chinese();

        // 先把机器级那一份真的装上（落点是临时目录里的替身，注册表根换成探针键）
        var installer = new InstallerWindow(lang, _machine, registryRoot: BothUsers,
            startsAsUninstaller: false, preselectedScope: InstallScope.AllUsers, elevate: null).Shown();
        UncheckExtras(installer);
        Click(installer, lang, "TEXT_BTN_INSTALL");
        WaitUntil(installer, lang, "TEXT_SETUP_DONE_TITLE");

        var asked = new List<InstallScope>();
        var window = new InstallerWindow(lang, BlockedMachine(), registryRoot: BothUsers,
            startsAsUninstaller: true, preselectedScope: null,
            elevate: scope => { asked.Add(scope); return false; }).Shown();

        Click(window, lang, "TEXT_BTN_UNINSTALL");

        Assert.Single(asked);
        Assert.Equal(InstallScope.AllUsers, asked[0]);

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.True(shown.Contains(lang.Label("TEXT_SETUP_CONFIRM_TITLE"), StringComparison.Ordinal),
            $"提权没成，就该停在确认那一页：{shown}");
        Assert.Contains(lang.Label("TEXT_SETUP_UNINSTALL_DENIED"), shown);
        Assert.DoesNotContain(lang.Label("TEXT_SETUP_WORKING_TITLE"), shown);

        // 界面上那句"什么都没动"要真的什么都没动
        Assert.True(File.Exists(_machine.ExecutablePath), "没拿到管理员权限，程序文件却已经删了。");
        Assert.True(Registry.CurrentUser.OpenSubKey(ProbeKey) is not null, "没拿到管理员权限，卸载表那条却已经删了。");
    }

    /// <summary>卸载这一路提权成功后，这一份同样该收窗退出，让提了权的那一个接着卸。</summary>
    [AvaloniaFact]
    public void HandingTheUninstallOverToAnElevatedInstanceClosesThisOne()
    {
        var lang = TestLang.Chinese();
        var closed = false;
        var window = new InstallerWindow(lang, BlockedMachine(), registryRoot: BothUsers,
            startsAsUninstaller: true, preselectedScope: null, elevate: _ => true).Shown();
        window.Closed += (_, _) => closed = true;

        Click(window, lang, "TEXT_BTN_UNINSTALL");

        Assert.True(closed, "卸载交出去了，这一份却还开着 —— 屏幕上会有两个卸载向导，用户不知道哪个在删。");
    }

    // ------------------------------------- 机器级那一条整条往返（根被换到探针键）

    /// <summary>
    /// 走完一次"为所有用户"的向导：程序文件、两处快捷方式、卸载表都按机器级那套落点落地，
    /// 卸载表里记下 <c>AndzipScope = Machine</c>；再从卸载向导走一遍，全部收回。
    /// <para>这条测的是<strong>接线</strong>：作用域有没有一路带到写注册表与建快捷方式那两处，
    /// 而不是只在界面上换了个单选钮。真的 <c>HKLM</c> 与 <c>Program Files</c> 由
    /// <see cref="BothUsers"/> 与临时目录替身顶替 —— 这台机器上的账户不是管理员，
    /// 真的那一处写不了，这一点在开发记录与发布说明里都写明白。</para>
    /// </summary>
    [AvaloniaFact]
    public void WalkingTheWizardAsMachineScopeInstallsAndUninstallsTheMachineCopy()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _machine, registryRoot: BothUsers,
            startsAsUninstaller: false, preselectedScope: null, elevate: null).Shown();

        Click(window, lang, "TEXT_BTN_NEXT");   // 欢迎 → 范围
        Click(window, lang, "TEXT_BTN_NEXT");   // 范围（机器级那份写得动 → 不提权）→ 位置
        UncheckExtras(window);
        Click(window, lang, "TEXT_BTN_INSTALL");

        var shown = WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");
        Assert.Contains(lang.Label("TEXT_INSTALLER_DONE"), shown);

        Assert.True(File.Exists(_machine.ExecutablePath), "机器级那份的程序文件不在 Program Files 的替身里。");
        Assert.True(File.Exists(_machine.StartMenuShortcut), "公共开始菜单里没有那一项。");
        Assert.True(File.Exists(_machine.StartMenuUninstallShortcut), "公共开始菜单里没有「卸载 Andzip」。");
        Assert.True(File.Exists(_machine.DesktopShortcut), "公共桌面上没有那一项。");
        Assert.True(File.Exists(_machine.UninstallScript), "机器级那一份的安装目录里没有 uninstall.bat。");

        using (var key = Registry.CurrentUser.OpenSubKey(ProbeKey))
        {
            Assert.Equal("Machine", key?.GetValue("AndzipScope"));
            Assert.Equal(_machine.InstallDirectory, key?.GetValue("InstallLocation"));
        }

        // 卸载：从同一个作用域进来，落点按记着的那一处收回
        var uninstaller = new InstallerWindow(
            lang, _machine, registryRoot: BothUsers, startsAsUninstaller: true,
            preselectedScope: null, elevate: null).Shown();
        Click(uninstaller, lang, "TEXT_BTN_UNINSTALL");
        WaitUntil(uninstaller, lang, "TEXT_SETUP_UNINSTALL_DONE_TITLE");

        Assert.False(File.Exists(_machine.ExecutablePath), "界面上说卸完了，机器级那份的程序文件还在。");
        Assert.False(File.Exists(_machine.StartMenuShortcut), "界面上说卸完了，公共开始菜单那一项还在。");
        Assert.True(Registry.CurrentUser.OpenSubKey(ProbeKey) is null, "界面上说卸完了，卸载表那条还在。");
    }

    /// <summary>
    /// 机器级那一份<strong>不许往个人目录里写任何东西</strong>。
    /// <para>提权之后那个进程的"当前用户"可能已经不是双击安装的人（用别的账户过 UAC 时就是），
    /// 混着写会得到"给所有人装了，却只有安装者看得见"。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheMachineCopyWritesNothingIntoAPersonalFolder()
    {
        var personal = Path.Combine(_root, "Local");

        Assert.DoesNotContain(personal, _machine.InstallDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(personal, _machine.StartMenuShortcut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(personal, _machine.DesktopShortcut!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.Combine(_root, "Roaming"), _machine.StartMenuShortcut, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一个写不进去的机器级落点（这台机器上没有 X: 盘）。</summary>
    private InstallPlan BlockedMachine() => _machine with
    {
        InstallDirectory = @"X:\Programs\Andzip",
        ExecutablePath = @"X:\Programs\Andzip\Andzip.exe",
    };

    // ---------------- 一台机器只留一份：另一个作用域里那一份要先卸掉 ----------------

    /// <summary>
    /// 两个作用域各自的 HKCU 子键。
    /// <para>与真的机器同构：<strong>相对键名是同一个，差别只在挂在哪个根上</strong> ——
    /// 所以 <see cref="InstallationService.InstalledCopy"/> 用"另一个作用域 + 同一个键名"去问，
    /// 问得到的就是另一份，问不到就是没有。用 <see cref="BothUsers"/> 那种"两个作用域同一个根"
    /// 反而测不出这一条（两份会重叠成一份）。</para>
    /// </summary>
    private static RegistryKey ScopeRoot(InstallScope scope)
        => Registry.CurrentUser.CreateSubKey(
            $@"{OneCopyRoot}\{(scope == InstallScope.AllUsers ? "Machine" : "User")}");

    private const string OneCopyRoot = @"Software\Andzip.Tests\OneCopy";
    private const string OneCopyEntry = "Entry";

    private InstallPlan One(InstallScope scope)
        => (scope == InstallScope.AllUsers ? _machine : _user) with { UninstallKey = OneCopyEntry };

    /// <summary>
    /// 装第二份之前，向导必须先把第一份说出来。
    /// <para>用户 2026-10-15 的话是「为所有用户安装和为我安装，会安装两份 App，这不对吧」——
    /// 截图里桌面上并排两个都叫 Andzip 的图标，属性页一个指向 Program Files、一个指向
    /// %LOCALAPPDATA%\Programs。两份同时在的代价不是磁盘，是这两行：<strong>没人分得清哪个是哪个</strong>。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheScopePageNamesTheOtherCopyAndOffersToRemoveIt()
    {
        var lang = TestLang.Chinese();
        var machine = One(InstallScope.AllUsers);

        InstallThrough(lang, machine);

        var window = new InstallerWindow(
            lang, One(InstallScope.CurrentUser), registryRoot: ScopeRoot,
            startsAsUninstaller: false, preselectedScope: null, elevate: null).Shown();
        Click(window, lang, "TEXT_BTN_NEXT");

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.True(shown.Contains(machine.InstallDirectory, StringComparison.Ordinal),
            $"范围那一页没说出另一份在哪：{shown}");

        var box = Assert.Single(VisualQuery.Of<CheckBox>(window), c => c.IsVisible);
        Assert.True(box.IsChecked == true, "「装之前先卸载那一份」默认就该开着 —— 那是这条流程的规矩。");

        window.Close();
    }

    /// <summary>
    /// 换作用域 = 换"对面那一份是谁"：这一行与那颗勾要跟着出现、跟着消失。
    /// <para>为什么单独一条：同组其余几条都只在<strong>进页面时那一个作用域</strong>下读了一次，
    /// 把两个单选钮的事件接线整段删掉它们照样全绿 —— 而那正是用户在真机上会撞的形状：
    /// 从「仅为我自己」切到「所有用户」，警告还写着上一作用域那一份的目录，
    /// 或者反过来，明明对面有一份却一个字不提，直接放过去装出第二份。</para>
    /// </summary>
    [AvaloniaFact]
    public void FlippingTheScopeRereadsWhichCopyIsTheOtherOne()
    {
        var lang = TestLang.Chinese();
        var user = One(InstallScope.CurrentUser);

        // 这台机器上只有「仅为我自己」那一份
        InstallThrough(lang, user);

        var window = new InstallerWindow(
            lang, user, registryRoot: ScopeRoot,
            startsAsUninstaller: false, preselectedScope: null, elevate: null).Shown();
        Click(window, lang, "TEXT_BTN_NEXT");

        Assert.DoesNotContain(VisualQuery.Of<CheckBox>(window), c => c.IsVisible);
        Assert.DoesNotContain(user.InstallDirectory,
            string.Join("|", VisualQuery.VisibleTexts(window)), StringComparison.Ordinal);

        // 切到「所有用户」：对面那一份就是刚装好的这一份，必须当场说出来
        Radios(window)[1].IsChecked = true;
        UiWait.Pump(80);

        var flipped = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.Contains(user.InstallDirectory, flipped, StringComparison.Ordinal);
        Assert.Single(VisualQuery.Of<CheckBox>(window), c => c.IsVisible);

        // 再切回去：不许留着上一作用域那一条（那是一句针对<strong>另一个目录</strong>的话）
        Radios(window)[0].IsChecked = true;
        UiWait.Pump(80);

        Assert.DoesNotContain(VisualQuery.Of<CheckBox>(window), c => c.IsVisible);
        Assert.DoesNotContain(user.InstallDirectory,
            string.Join("|", VisualQuery.VisibleTexts(window)), StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// 不卸另一份就不给继续。
    /// <para>这里挡住而不是"随你便"：一台机器一份是这条流程的规矩，
    /// 真需要两份并存的人该用绿色版（那个目录谁都不写）。
    /// 停下来的位置也要对 —— 跳过这一步去装，等于那条警告白写。</para>
    /// </summary>
    [AvaloniaFact]
    public void RefusingToRemoveTheOtherCopyStopsTheWizard()
    {
        var lang = TestLang.Chinese();
        var machine = One(InstallScope.AllUsers);
        var user = One(InstallScope.CurrentUser);

        InstallThrough(lang, machine);

        var window = new InstallerWindow(
            lang, user, registryRoot: ScopeRoot,
            startsAsUninstaller: false, preselectedScope: null, elevate: null).Shown();
        Click(window, lang, "TEXT_BTN_NEXT");

        Assert.Single(VisualQuery.Of<CheckBox>(window), c => c.IsVisible).IsChecked = false;
        Click(window, lang, "TEXT_BTN_NEXT");

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.True(shown.Contains(lang.Label("TEXT_SETUP_SCOPE_TITLE"), StringComparison.Ordinal),
            $"不同意卸掉另一份，就该停在范围那一页：{shown}");
        Assert.Contains(lang.Label("TEXT_SETUP_REPLACE_OTHER_REQUIRED"), shown);
        Assert.False(File.Exists(user.ExecutablePath), "被挡住了却还是开始装了。");

        window.Close();
    }

    /// <summary>
    /// 同意之后：进度页第一步先卸另一份，再装这一份，最后这台机器上只剩这一份。
    /// <para>顺序要紧 —— 先装再卸会把刚装好的那一份的注册表项一起收掉（两个作用域在测试里
    /// 是两把不同的键，在真机上是同一个相对键名挂在两个根上）。</para>
    /// <para>这一条只断言<strong>程序文件与卸载表那两处</strong>：测试里"另一份的落点"是拿
    /// 当前这一份的 <c>_given</c> 改出来的（那是让整条往返不碰开发机的唯一办法），
    /// 所以另一份的快捷方式路径在测试里不是它真装时那一条。真的那一处由
    /// <c>PlanForScope → CurrentPlan(otherScope, dir)</c> 在运行时算，见开发记录第 69 条。</para>
    /// </summary>
    [AvaloniaFact]
    public void AgreeingRemovesTheOtherCopyBeforeInstallingThisOne()
    {
        var lang = TestLang.Chinese();
        var machine = One(InstallScope.AllUsers);
        var user = One(InstallScope.CurrentUser);

        InstallThrough(lang, machine);
        Assert.True(File.Exists(machine.ExecutablePath), "前提：另一份没装出来。");

        var window = new InstallerWindow(
            lang, user, registryRoot: ScopeRoot,
            startsAsUninstaller: false, preselectedScope: null, elevate: null).Shown();
        Click(window, lang, "TEXT_BTN_NEXT");

        Click(window, lang, "TEXT_BTN_NEXT");   // 范围（勾着"先卸另一份"）→ 位置
        UncheckExtras(window);
        Click(window, lang, "TEXT_BTN_INSTALL");

        var shown = WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");
        Assert.True(shown.Contains(lang.Label("TEXT_SETUP_DONE_TITLE"), StringComparison.Ordinal));
        Assert.False(File.Exists(machine.ExecutablePath), "说好了先卸另一份，Program Files 的替身里那一份还在。");
        Assert.True(Registry.CurrentUser.OpenSubKey($@"{OneCopyRoot}\Machine\{OneCopyEntry}") is null,
            "另一份的卸载表还在 —— 卸载表里会同时出现两条 Andzip。");
        Assert.True(File.Exists(user.ExecutablePath), "这一份没装上。");

        window.Close();
    }

    /// <summary>走完一次向导装好一份（两个勾选项关掉，免得动开发机上的关联与右键菜单）。</summary>
    private void InstallThrough(LangCatalog lang, InstallPlan plan)
    {
        var window = new InstallerWindow(
            lang, plan, registryRoot: ScopeRoot,
            startsAsUninstaller: false, preselectedScope: null, elevate: null).Shown();

        Click(window, lang, "TEXT_BTN_NEXT");
        Click(window, lang, "TEXT_BTN_NEXT");
        UncheckExtras(window);
        Click(window, lang, "TEXT_BTN_INSTALL");
        WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");
        window.Close();
    }

    // --------------------------------------------------------------- 小工具

    private static IReadOnlyList<RadioButton> Radios(InstallerWindow window)
        => VisualQuery.Of<RadioButton>(window).Where(r => r.IsVisible).ToList();

    private static void UncheckExtras(InstallerWindow window)
    {
        foreach (var row in VisualQuery.Of<SettingToggle>(window))
        {
            row.IsChecked = false;
        }
    }

    private static void Click(InstallerWindow window, LangCatalog lang, string labelKey)
    {
        var button = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label(labelKey)));

        Clicker.Invoke(button);
        UiWait.Pump(80);
    }

    private static string WaitUntil(InstallerWindow window, LangCatalog lang, string doneKey)
    {
        var deadline = Environment.TickCount64 + 30_000;
        var shown = string.Empty;

        while (Environment.TickCount64 < deadline)
        {
            UiWait.Pump(50);
            shown = string.Join("|", VisualQuery.VisibleTexts(window));
            if (shown.Contains(lang.Label(doneKey), StringComparison.Ordinal))
            {
                return shown;
            }
        }

        Assert.Fail($"向导没走到「{lang.Label(doneKey)}」，屏幕上停在：{shown}");
        return shown;
    }
}
