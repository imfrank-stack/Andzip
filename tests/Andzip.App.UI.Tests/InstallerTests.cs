using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Win32;

namespace Andzip.App.UiTests;

/// <summary>
/// 「正常安装」这条路径的往返实测：真的装一次，看桌面/开始菜单/卸载表几处都落了东西，
/// 再真的卸一次，看它们都收回去了。
/// <para>为什么不用假的文件系统：快捷方式是 shell 的 <c>.lnk</c> 二进制格式，
/// 卸载表是注册表 —— 这两样"代码写了"与"系统里真有"是两件事，
/// 而它们的失败方式恰好都是"用户看不见任何报错，只是没有图标"。</para>
/// <para>落点全部指向临时目录，注册表写的是探针键；<c>associate</c> 与 <c>contextMenu</c>
/// 一律传 false —— 那两条会动开发机上的真实关联表，测试不该有那种副作用。</para>
/// <para>界面上那几条走的是<strong>用户那条路</strong>：欢迎页 → 位置页 → 安装 → 完成页。
/// 2.0.0 之前这里只有一屏，测试也就只按一颗按钮；现在每一页都有"上一/下一"的走法，
/// 少一步都会露出来（比如位置页没走到就直接装，等于那个可选目录是假的）。</para>
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------- 服务层：真的装、真的卸

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

    /// <summary>
    /// 开始菜单里必须有「卸载 Andzip」那一项，而且它点开的是<strong>卸载</strong>，不是主窗口。
    /// <para>用户的原话是"你这样都没有卸载 uninstall 和开始菜单等"。注册表里那条卸载项其实一直在，
    /// 但只有会打开「设置 → 应用」的人才找得到；开始菜单这一项才是顺手的那一个。
    /// 目标与参数两格都得读回来核对 —— 指错或丢了参数时它照样显示得好好的。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void StartMenuCarriesAnUninstallEntryThatActuallyUninstalls()
    {
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        Assert.True(File.Exists(_plan.StartMenuUninstallShortcut), "开始菜单里没有「卸载 Andzip」那一项。");

        var (target, arguments) = ShellLink.ReadTarget(_plan.StartMenuUninstallShortcut);
        Assert.Equal(_plan.ExecutablePath, target);
        Assert.Contains("/uninstall", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// 卸载表里的版本号不能带 SourceLink 那段提交号。
    /// <para>1.9.2 真机核对：系统的「设置 → 应用」里显示的是
    /// <c>1.9.2+45.718056ece53be97a34004652df38b3af68954316</c>。标题栏那一处 1.9.1 已经修过，
    /// 这一处读的是复制过去那个 exe 的版本信息，所以得另外切一刀。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void UninstallEntryRecordsAVersionWithoutTheSourceLinkSuffix()
    {
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        using var key = Registry.CurrentUser.OpenSubKey(_plan.UninstallKey);
        var version = (string?)key?.GetValue("DisplayVersion") ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(version), "卸载表里没有版本号。");
        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
    }

    /// <summary>
    /// 安装目录里必须有一个<strong>双击就能卸载</strong>的东西（2.4.0）。
    /// <para>用户的原话是「咱们的安装包只有一个 exe 文件，这不对，至少增加一个
    /// uninstall.exe/uninstall.bat」。真卸逻辑在 <c>Andzip.exe /uninstall</c> 里，
    /// 所以出的是一个批处理壳 —— 一个真的 <c>uninstall.exe</c> 要么是那 20 MB 的第二份拷贝，
    /// 要么也是这样一个壳，而壳不需要目标机器上有编译器。</para>
    /// <para>批处理的内容必须整个是 ASCII：<c>cmd</c> 按活动代码页解码这个文件，注释里塞中文
    /// 会先在屏幕上变成一串 <c>?</c>。所以这条量的是<strong>源文本</strong>而不是落盘那一份 ——
    /// 写的时候用的是 <c>Encoding.ASCII</c>，盘上那一份天生没有非 ASCII 字符，量它等于什么都没量。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheInstallFolderCarriesADoubleClickableUninstallFile()
    {
        Assert.All(InstallLayout.UninstallScriptText,
            c => Assert.True(c < 128, $"批处理源码里有非 ASCII 字符「{c}」，cmd 会读成一串问号"));

        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        Assert.True(File.Exists(_plan.UninstallScript),
            $"安装目录里没有 {InstallLayout.UninstallScriptName}：{Path.GetDirectoryName(_plan.ExecutablePath)}");
        Assert.Equal(InstallLayout.UninstallScriptText, File.ReadAllText(_plan.UninstallScript));

        var text = File.ReadAllText(_plan.UninstallScript);

        Assert.Contains("@echo off", text, StringComparison.Ordinal);
        Assert.Contains($"\"%~dp0{InstallLayout.ExeName}\" /uninstall", text, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void UninstallTakesEverythingBackOut()
    {
        var service = Service();
        service.Install(_plan, SourceExe(), associate: false, contextMenu: false);

        Assert.True(service.Uninstall(_plan), "卸载报告说删干净了，但安装目录还在（或反过来）。");

        Assert.False(Directory.Exists(_plan.InstallDirectory), "安装目录还在。");
        Assert.False(File.Exists(_plan.UninstallScript), "安装目录里那个双击就能卸载的批处理还在。");
        Assert.False(File.Exists(_plan.StartMenuShortcut), "开始菜单的快捷方式还在。");
        Assert.False(File.Exists(_plan.DesktopShortcut!), "桌面的快捷方式还在。");
        Assert.False(File.Exists(_plan.StartMenuUninstallShortcut), "开始菜单里「卸载 Andzip」那一项还在。");
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
    /// 卸载表里的版本号不能带 SourceLink 那段提交号。
    /// <para>1.9.2 真机核对：系统的「设置 → 应用」里显示的是
    /// <c>1.9.2+45.718056ece53be97a34004652df38b3af68954316</c>。标题栏那一处 1.9.1 已经修过，
    /// 这一处读的是复制过去那个 exe 的版本信息，所以得另外切一刀。</para>
    /// <para>上面那条测的是"写进注册表的这一串里没有加号"，但测试用的源文件是一个 4 字节的假货，
    /// 它根本没有版本资源 —— 所以那条即使把这一刀漏了也照样绿。这一条直接量切本身。</para>
    /// </summary>
    [Theory]
    [InlineData("1.9.2+45.718056ece53be97a34004652df38b3af68954316", "1.9.2")]
    [InlineData("2.0.0", "2.0.0")]
    [InlineData(" 1.9.2 ", "1.9.2")]
    [InlineData("+45.abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheVersionWrittenToTheUninstallEntryLosesTheSourceLinkSuffix(string? product, string? expected)
    {
        var clean = InstallationService.CleanVersion(product);

        Assert.DoesNotContain("+", clean, StringComparison.Ordinal);

        // 读不到版本时退回自己这份程序集的版本，而不是留一个空字符串给「设置 → 应用」显示
        Assert.Equal(expected ?? typeof(InstallationService).Assembly.GetName().Version?.ToString() ?? "0.0.0", clean);
    }

    /// <summary>
    /// 卸不掉的时候要说"留到退出后删"，不能把异常原文甩到界面上。
    /// <para>1.9.2 真机核对抓到的正题：从<strong>已安装的那一份</strong>里卸载（控制面板那条路）时，
    /// 删自己正在跑的那个 exe 报的是 <c>UnauthorizedAccessException</c>，而代码只接
    /// <c>IOException</c> —— 于是延迟删除根本没启动，程序文件留在盘上，界面上是一行英文原文
    /// "Access to the path 'Andzip.exe' is denied."。这里用只读属性造出同一个异常：
    /// 只读文件删不掉时 .NET 报的正是 <c>UnauthorizedAccessException</c>。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AProgramFileItCannotDeleteIsDeferredInsteadOfThrowing()
    {
        var service = Service();
        service.Install(_plan, SourceExe(), associate: false, contextMenu: false);

        File.SetAttributes(_plan.ExecutablePath, FileAttributes.ReadOnly);

        var cleaned = service.Uninstall(_plan);

        Assert.False(cleaned, "文件删不掉却报告「删干净了」，那就是谎报。");
        Assert.True(File.Exists(_plan.ExecutablePath), "延迟删除不该当场把文件删掉。");

        File.SetAttributes(_plan.ExecutablePath, FileAttributes.Normal);
    }

    /// <summary>
    /// 装到自定义目录之后，卸载要按卸载表里记着的那个位置找回自己。
    /// <para>只看默认路径的后果是"卸完了 D:\Tools\Andzip 还留在那儿，而开始菜单与注册表里的
    /// 东西没了" —— 半截卸载比不卸更难看。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void UninstallGoesBackToTheRecordedDirectoryRatherThanTheDefaultOne()
    {
        var service = Service();
        var elsewhere = Path.Combine(_root, "自定义 位置", "Andzip");
        var custom = _plan with
        {
            InstallDirectory = elsewhere,
            ExecutablePath = Path.Combine(elsewhere, InstallLayout.ExeName),
        };

        service.Install(custom, SourceExe(), associate: false, contextMenu: false);

        using (var key = Registry.CurrentUser.OpenSubKey(_plan.UninstallKey))
        {
            Assert.Equal(elsewhere, key?.GetValue("InstallLocation"));
        }

        // 探针键里记的是临时目录，所以这里只核对"读回来的那一个是不是它"，
        // 真删仍然用 custom 那份（RecordedPlan 会按这台机器的真实目录算快捷方式）。
        Assert.Equal(elsewhere, service.RecordedPlan(_plan.UninstallKey).InstallDirectory);

        Assert.True(service.Uninstall(custom));
        Assert.False(Directory.Exists(elsewhere), "自定义目录没被收回去。");
    }

    /// <summary>安装进行中的每一步都要报出来，而且百分比是往前走的。</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void InstallReportsEachStepSoTheWizardCanSayWhatItIsDoing()
    {
        var seen = new List<ProgressStep>();
        var service = Service();

        service.Install(_plan, SourceExe(), associate: false, contextMenu: false, new Collect(seen));

        // 没勾关联与右键菜单时就该只有这三步：没做的事不能占一格里程碑，
        // 否则那根条永远走不完，用户以为卡住了。
        Assert.Equal(
            new[] { "TEXT_SETUP_STEP_COPY", "TEXT_SETUP_STEP_LINKS", "TEXT_SETUP_STEP_REGISTRY" },
            seen.Select(step => step.MessageKey).ToArray());
        Assert.Equal(0, seen[0].Percent);
        Assert.True(seen[^1].Percent > seen[0].Percent, "进度条原地不动，那根条就是假的。");
        Assert.All(seen, step => Assert.InRange(step.Percent, 0, 100));

        // 卸载那一路最后一步必须是"删程序文件"：它前面那些都在动快捷方式与注册表，
        // 顺序倒了会把还在用的东西先删掉。
        var back = new List<ProgressStep>();
        service.Uninstall(_plan, new Collect(back));
        Assert.Equal("TEXT_SETUP_STEP_UNFILES", back[back.Count - 1].MessageKey);
    }

    /// <summary>把报出来的每一步收进一个列表，好让上面的断言看得见"走到哪一步了"。</summary>
    private sealed class Collect(List<ProgressStep> into) : IProgress<ProgressStep>
    {
        public void Report(ProgressStep value) => into.Add(value);
    }

    /// <summary>
    /// "等退出再删"这条命令等的是<strong>这个进程真的没了</strong>，不是睡两秒。
    /// <para>1.9.2 那版是 <c>ping -n 3</c> 之后 <c>rmdir</c>：界面上那句"已安排在退出后删除"
    /// 说的是前者，做的却是后者。用户在完成页上多停几秒，rmdir 就已经在还锁着的文件上
    /// 失败过了，目录永远留在盘上（2.0.0 真机核对：卸完之后程序文件还在）。</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheDeferredDeleteWaitsForThisProcessRatherThanForAGuess()
    {
        var script = InstallationService.DeleteArguments(4321, @"C:\Users\frank\AppData\Local\Programs\Andzip");

        // 开头也得钉住：2.0.0 第一版在这里留了一个 cmd 写法剩下的 /c，PowerShell 把它当成
        // 要执行的命令名，于是"已安排在退出后删除"这句话永远兑不了现
        Assert.StartsWith("-NoProfile", script, StringComparison.Ordinal);
        Assert.Contains("Wait-Process -Id 4321", script, StringComparison.Ordinal);
        Assert.Contains(@"Remove-Item -LiteralPath 'C:\Users\frank\AppData\Local\Programs\Andzip'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ping", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 安装目录现在是用户能自己打的东西，所以它必须整个待在单引号字面量里，
    /// 里面的单引号按 PowerShell 的规矩翻倍 —— 否则一个带 <c>'</c> 的目录名就能把这条命令改成别的。
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AQuotedInstallDirectoryCannotRewriteTheDeferredDeleteCommand()
    {
        var script = InstallationService.DeleteArguments(7, @"D:\it's a 目录\Andzip");

        Assert.Contains(@"'D:\it''s a 目录\Andzip'", script, StringComparison.Ordinal);

        // 整条命令里只该有那两对定界用的单引号：路径自己没漏出任何一撇
        Assert.Equal(4, script.Count(c => c == '\''));
    }

    // ------------------------------------------------------- 界面：向导走法

    /// <summary>
    /// 打开就是欢迎页：说清要做什么，给「下一步」；这台机器没装过就不该看见那颗红色「卸载」。
    /// </summary>
    [AvaloniaFact]
    public void TheInstallerOpensOnAWelcomePageAndHidesUninstallWhenNothingIsInstalled()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang).Shown();

        var titles = VisualQuery.VisibleTexts(window);
        Assert.Contains(lang.Label("TEXT_SETUP_WELCOME_TITLE"), titles);
        Assert.Contains(lang.Label("TEXT_BTN_NEXT"), titles);
        Assert.Contains(lang.Label("TEXT_BTN_CANCEL"), titles);

        // 这条量的是"开发机上装没装"，所以钉的是相关性而不是某一侧：
        // 那颗红色按钮必须在装过的时候才出现（没东西可删时摆着它更让人犹豫）。
        var installed = File.Exists(new InstallationService(lang).CurrentPlan().ExecutablePath);
        Assert.Equal(installed, titles.Contains(lang.Label("TEXT_BTN_UNINSTALL")));

        // 标题栏那一句要跟着身份走：从控制面板进来卸载时窗口不该还写着"安装 Andzip"
        Assert.Equal(lang.Label("TEXT_TITLE_INSTALLER"), window.Title);
    }

    /// <summary>已经装过时，欢迎页上才有那颗红色「卸载」。</summary>
    [AvaloniaFact]
    public void TheWelcomePageOffersUninstallOnceSomethingIsInstalled()
    {
        var lang = TestLang.Chinese();
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var titles = VisualQuery.VisibleTexts(new InstallerWindow(lang, _plan).Shown());

        Assert.Contains(lang.Label("TEXT_BTN_UNINSTALL"), titles);
        Assert.Contains(lang.Format("TEXT_INSTALLER_ALREADY", ("dir", _plan.ExecutablePath)), titles);
    }

    /// <summary>
    /// 位置那一页：可改的路径框 + 所需空间 + 可用空间 + 附加任务，与 Inno 那张截图对上。
    /// </summary>
    [AvaloniaFact]
    public void TheLocationPageShowsThePathAndBothSpaceNumbers()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);

        var titles = VisualQuery.VisibleTexts(window);
        Assert.Contains(lang.Label("TEXT_SETUP_LOCATION_TITLE"), titles);
        Assert.Contains(lang.Label("TEXT_INSTALLER_TARGET"), titles);
        Assert.Contains(lang.Label("TEXT_SETUP_SPACE_REQUIRED"), titles);
        Assert.Contains(lang.Label("TEXT_SETUP_SPACE_AVAILABLE"), titles);
        Assert.Contains(lang.Label("TEXT_SETUP_OPTIONS_TITLE"), titles);
        Assert.Contains(lang.Label("TEXT_INSTALLER_ASSOCIATE"), titles);
        Assert.Contains(lang.Label("TEXT_INSTALLER_CONTEXT"), titles);
        Assert.Contains(lang.Label("TEXT_BTN_BACK"), titles);
        Assert.Contains(lang.Label("TEXT_BTN_INSTALL"), titles);

        var box = VisualQuery.Of<TextBox>(window).Single();
        Assert.Equal(_plan.InstallDirectory, box.Text);

        // 两个附加任务的初值跟着"这台机器装没装过"走：没装过就该是开着的。
        // 2.0.0 真机核对时它们是灰的 —— 初值判据问的是 IsChecked 是不是 null，
        // 而 ToggleButton 的初值是 false，那个判断永远不成立，于是新装的人默认什么都不做。
        var rows = VisualQuery.Of<SettingToggle>(window).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsChecked == true, $"「{row.Label}」这一项这时应该是开着的"));

        // 所需空间不能是"–"或 0：它量的是真的那个源文件的字节数
        var values = VisualQuery.Of<TextBlock>(window).Select(t => t.Text).ToList();
        Assert.Contains(SizeText.Format(InstallTarget.RequiredBytes(Environment.ProcessPath!)), values);
    }

    /// <summary>
    /// 已经装过时，两个附加任务默认<strong>不</strong>打开。
    /// <para>那时点「安装」是要重装/补关联，悄悄再改一遍他的默认程序设置是意外后果
    /// （与 1.8.x 那版界面同一个约定，只是那时候它写在构造里，没人测过）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheExtrasStartOffWhenReinstallingOverAnExistingInstall()
    {
        var lang = TestLang.Chinese();
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var window = new InstallerWindow(lang, _plan).Shown();
        ToLocationPage(window, lang);

        var rows = VisualQuery.Of<SettingToggle>(window).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsChecked == true, $"重装时不该默认再动一遍他的系统设置：「{row.Label}」"));
    }

    /// <summary>
    /// 上一步 / 下一步来回走，不该把用户刚改过的选择抹掉。
    /// </summary>
    [AvaloniaFact]
    public void WalkingBackAndForthKeepsTheChoicesTheUserMade()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        var rows = VisualQuery.Of<SettingToggle>(window).ToList();
        Assert.Equal(2, rows.Count);
        rows[0].IsChecked = false;
        rows[1].IsChecked = false;

        // 位置页的「上一步」回到范围页，再「下一步」走回来
        Click(window, lang, "TEXT_BTN_BACK");
        Click(window, lang, "TEXT_BTN_NEXT");

        var again = VisualQuery.Of<SettingToggle>(window).ToList();
        Assert.Equal(2, again.Count);
        Assert.All(again, row => Assert.False(row.IsChecked == true,
            $"回到位置页时，用户关掉的那一项被偷偷打开了：「{row.Label}」"));
    }

    /// <summary>
    /// 真的走完一次向导：欢迎 → 范围 → 位置 → 安装 → 完成，而且磁盘上真有东西。
    /// <para>1.8.1 装不上那个形状（后台线程读 <c>SettingToggle.IsChecked</c> 抛
    /// "Call from invalid thread"）只有走这条路才拦得住。</para>
    /// </summary>
    [AvaloniaFact]
    public void WalkingTheWizardActuallyInstallsAndSaysSoOnTheFinishPage()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        UncheckExtras(window);
        Click(window, lang, "TEXT_BTN_INSTALL");

        var shown = WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");

        Assert.Contains(lang.Label("TEXT_INSTALLER_DONE"), shown);
        Assert.Contains(lang.Label("TEXT_SETUP_UNINSTALL_HINT"), shown);
        Assert.DoesNotContain("invalid thread", shown, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_plan.ExecutablePath), "完成页说装好了，可安装目录里没有程序文件。");
        Assert.True(File.Exists(_plan.StartMenuShortcut), "完成页说装好了，可开始菜单没有快捷方式。");
        Assert.True(File.Exists(_plan.StartMenuUninstallShortcut), "完成页说装好了，可开始菜单没有「卸载 Andzip」。");
        Assert.True(File.Exists(_plan.UninstallScript), "完成页说装好了，可安装目录里没有 uninstall.bat。");
    }

    /// <summary>
    /// 取不到桌面目录时，完成页那句回话<strong>不能提桌面</strong>。
    /// <para>1.8.4 真机核对抓到的就是这一类：装完显示"桌面与开始菜单的快捷方式已创建"，
    /// 桌面上却什么也没有 —— 因为 <c>FOLDERID_Desktop</c> 的 GUID 凭记忆写错了，
    /// 桌面路径解析成 null，那一枚快捷方式被静默跳过，而回话是写死的。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheFinishPageStopsClaimingTheDesktopShortcutWhenThereIsNoDesktopFolder()
    {
        var lang = TestLang.Chinese();
        var noDesktop = InstallLayout.Plan(
            Path.Combine(_root, "Local"), Path.Combine(_root, "Roaming"), desktop: null)
            with { UninstallKey = _plan.UninstallKey };

        var window = new InstallerWindow(lang, noDesktop).Shown();
        ToLocationPage(window, lang);
        UncheckExtras(window);
        Click(window, lang, "TEXT_BTN_INSTALL");

        var shown = WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");

        Assert.True(File.Exists(noDesktop.StartMenuShortcut), "开始菜单那一个也没建出来，那就不是桌面目录的事了。");
        Assert.True(shown.Contains(lang.Label("TEXT_INSTALLER_DONE_NO_DESKTOP"), StringComparison.Ordinal),
            $"界面没有照实说「{lang.Label("TEXT_INSTALLER_DONE_NO_DESKTOP")}」，实际是：{shown}");
        Assert.DoesNotContain(lang.Label("TEXT_INSTALLER_DONE"), shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在位置那一页改目录，装完东西就落在那一个，卸载表里记的也是那一个。
    /// <para>这条是"可选安装位置"不是装饰的凭据：只把路径显示出来、装的时候仍然用默认目录，
    /// 界面上看不出任何区别。</para>
    /// </summary>
    [AvaloniaFact]
    public void TypingADifferentFolderInstallsThere()
    {
        var lang = TestLang.Chinese();
        var elsewhere = Path.Combine(_root, "另一个位置");
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        UncheckExtras(window);
        VisualQuery.Of<TextBox>(window).Single().Text = elsewhere;
        Click(window, lang, "TEXT_BTN_INSTALL");

        WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");

        Assert.True(File.Exists(Path.Combine(elsewhere, InstallLayout.ExeName)),
            "改了位置却还装在原来的地方。");
        Assert.False(File.Exists(_plan.ExecutablePath), "默认目录里不该有那一个文件。");

        using var key = Registry.CurrentUser.OpenSubKey(_plan.UninstallKey);
        Assert.Equal(InstallTarget.Normalize(elsewhere), key?.GetValue("InstallLocation"));
    }

    /// <summary>
    /// 写一个写不进去的位置：停在原地说明白，不要开始装。
    /// <para>不等进度条走到一半才报错的理由：那一处会留下一个装了一半的目录，
    /// 而我们没有回滚可做（只会复制一个文件，不会先备份旧的）。</para>
    /// </summary>
    [AvaloniaFact]
    public void AnUnwritableLocationStopsOnThatPageInsteadOfInstalling()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        UncheckExtras(window);
        VisualQuery.Of<TextBox>(window).Single().Text = @"X:\Andzip 装不进来";
        Click(window, lang, "TEXT_BTN_INSTALL");

        UiWait.Pump(200);
        var shown = string.Join("|", VisualQuery.VisibleTexts(window));

        Assert.Contains(lang.Label("TEXT_SETUP_LOCATION_TITLE"), shown);
        Assert.True(shown.Contains(lang.Format("TEXT_SETUP_DIR_UNWRITABLE", ("dir", _plan.InstallDirectory)),
                StringComparison.Ordinal),
            $"位置没通过校验时要把原因说清楚，屏幕上却是：{shown}");
        Assert.False(File.Exists(_plan.ExecutablePath), "位置没通过校验却还是装了。");
    }

    /// <summary>
    /// 卸载走的是另一套页：确认 → 进行中 → 完成，标题也换成「卸载 Andzip」。
    /// <para>带 <c>/uninstall</c> 进来时（控制面板那条卸载命令、开始菜单那一项）直接开在这一页 ——
    /// 让人家先看见一个安装向导再自己找红色按钮，就是"没有 uninstall"那句抱怨的一半原因。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheUninstallerOpensOnAConfirmPageAndRemovesEverything()
    {
        var lang = TestLang.Chinese();
        var service = Service();
        service.Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var window = new InstallerWindow(lang, _plan, startsAsUninstaller: true).Shown();

        Assert.Equal(lang.Label("TEXT_TITLE_UNINSTALLER"), window.Title);
        var before = VisualQuery.VisibleTexts(window);
        Assert.Contains(lang.Label("TEXT_SETUP_CONFIRM_TITLE"), before);
        Assert.Contains(lang.Label("TEXT_BTN_UNINSTALL"), before);

        Click(window, lang, "TEXT_BTN_UNINSTALL");
        var shown = WaitUntil(window, lang, "TEXT_SETUP_UNINSTALL_DONE_TITLE");

        Assert.Contains(lang.Label("TEXT_INSTALLER_REMOVED"), shown);

        // 刚卸完的那一页不该再问"完成后运行 Andzip"，也不该再教人去哪儿卸载：
        // 那颗勾选框按下去什么也不会发生（Finish() 里挡着），而一颗不动的控件比少一颗更糟。
        Assert.DoesNotContain(lang.Label("TEXT_SETUP_RUN_AFTER"), shown);
        Assert.DoesNotContain(lang.Label("TEXT_SETUP_UNINSTALL_HINT"), shown);

        Assert.False(File.Exists(_plan.StartMenuShortcut), "界面上说卸完了，开始菜单的快捷方式还在。");
        Assert.False(File.Exists(_plan.StartMenuUninstallShortcut), "界面上说卸完了，「卸载 Andzip」那一项还在。");
        Assert.True(Registry.CurrentUser.OpenSubKey(_plan.UninstallKey) is null, "界面上说卸完了，卸载表里那条还在。");
    }

    /// <summary>
    /// 从安装向导的欢迎页翻到卸载：那颗红色按钮不该另开一个窗口，也不该丢下落点。
    /// </summary>
    [AvaloniaFact]
    public void TheUninstallButtonOnTheWelcomePageWalksIntoTheUninstaller()
    {
        var lang = TestLang.Chinese();
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var window = new InstallerWindow(lang, _plan).Shown();
        Click(window, lang, "TEXT_BTN_UNINSTALL");

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.Contains(lang.Label("TEXT_SETUP_CONFIRM_TITLE"), shown);
        Assert.Equal(lang.Label("TEXT_TITLE_UNINSTALLER"), window.Title);
    }

    /// <summary>
    /// 卸完之后，"这台机器上已经装过"那一句必须跟着改口。
    /// <para>真机截图里抓到的矛盾：红色卸载按钮按磁盘状态自己消失了，同一屏上那句
    /// "这台机器上已经装过 C:\…\Andzip.exe" 却还留着 —— 一件事的两个说法，
    /// 一个跟着状态走一个不走，比两个都不走更容易骗到人。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheWelcomePageStopsClaimingInstalledAfterUninstallingFromTheUi()
    {
        var lang = TestLang.Chinese();
        Service().Install(_plan, SourceExe(), associate: false, contextMenu: false);

        var window = new InstallerWindow(lang, _plan).Shown();
        Assert.Contains(lang.Format("TEXT_INSTALLER_ALREADY", ("dir", _plan.ExecutablePath)),
            VisualQuery.VisibleTexts(window));

        Click(window, lang, "TEXT_BTN_UNINSTALL");
        Click(window, lang, "TEXT_BTN_UNINSTALL");
        WaitUntil(window, lang, "TEXT_SETUP_UNINSTALL_DONE_TITLE");

        Assert.False(File.Exists(_plan.ExecutablePath), "界面上说卸完了，程序文件还在。");
        var reopened = string.Join("|", VisualQuery.VisibleTexts(new InstallerWindow(lang, _plan).Shown()));
        Assert.True(reopened.Contains(lang.Label("TEXT_INSTALLER_NOT_YET"), StringComparison.Ordinal),
            $"东西已经删干净了，重新打开的欢迎页却还写着装过：{reopened}");
    }

    /// <summary>
    /// 进行中那一页不给任何可点的动作：复制一个文件停不下来，
    /// 放一颗"点了没用的取消"比不放更让人犹豫（1.9.2 删掉进度框那颗被切掉的按钮是同一件事）。
    /// <para>这里按完按钮<strong>立刻</strong>看，一次都不 pump：处理器是 async void，
    /// 切到后台线程之前那段（把页翻到「正在安装」）是同步跑完的，而 4 个字节的复制
    /// 在下一个 pump 里就能结束 —— 晚看一眼，这一页就已经翻过去了。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheWorkingPageShowsTheStepAndNoButtons()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        UncheckExtras(window);

        var install = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_BTN_INSTALL")));
        Clicker.Invoke(install);

        var shown = string.Join("|", VisualQuery.VisibleTexts(window));
        Assert.Contains(lang.Label("TEXT_SETUP_WORKING_TITLE"), shown);
        Assert.Single(VisualQuery.Of<ProgressBar>(window));

        // 按文字量而不是按"有没有按钮"：标题栏那几枚图标按钮也是 Button，
        // 而"正在安装"里含着"安装"两个字，拿子串比会自己骗自己。
        var labels = VisualQuery.Of<Button>(window)
            .Select(VisualQuery.TextOf)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();
        Assert.DoesNotContain(lang.Label("TEXT_BTN_CANCEL"), labels);
        Assert.DoesNotContain(lang.Label("TEXT_BTN_INSTALL"), labels);
        Assert.DoesNotContain(lang.Label("TEXT_BTN_FINISH"), labels);

        WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");
        Assert.True(File.Exists(_plan.ExecutablePath), "走完进度却没装上的话，那根条就是装饰。");
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

    /// <summary>
    /// 页脚<strong>紧跟正文</strong>，多余的那一截留在页脚下面（3.6.0）。
    /// <para>用户 2026-10-16 的话：「这些按钮太靠下了」。安装完成那一页正文很短，而窗口的高度是
    /// <strong>只长不缩</strong>的（<c>InstallerWindow.HoldHeight</c> 那条规矩），
    /// 于是「完成」以前被顶在卡片最下沿、上面空一大截。现在它紧跟正文：同一颗位置的按钮
    /// 在「选定安装位置」与「安装完成」两页上的 y 必须差得出来。</para>
    /// <para>把布局改回"多余的高度留在正文那一格"（<c>RowDefinitions = "*,Auto"</c>），
    /// 两处 y 又会一样，这条立刻红。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheFooterFollowsTheContentInsteadOfStickingToTheBottom()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        UncheckExtras(window);
        var installTop = TopOf(window, lang, "TEXT_BTN_INSTALL");

        Click(window, lang, "TEXT_BTN_INSTALL");
        WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");

        var finishTop = TopOf(window, lang, "TEXT_BTN_FINISH");
        Assert.True(finishTop < installTop - 100,
            $"「安装」那一格在 y={installTop:0.#}，「完成」在 y={finishTop:0.#} —— "
            + "两者几乎一样高，说明页脚还是被顶在窗口最下面（用户要的是紧跟正文）");
    }

    /// <summary>
    /// 换页时<strong>窗口的内容区只长不缩</strong>（2.4.0 立的那条规矩，3.6.0 换了实现）。
    /// <para>真机核对里抓到的形状：点完「安装」，完成页一闪就没，向导自己关了，还顺手把 Andzip 启动了。
    /// 因由是这扇窗按内容自适应高度，进度页比位置页矮两百多像素 —— 页面一换窗口就缩，
    /// 用户那一下<strong>松手</strong>落在了窗口外面，Avalonia 于是以为左键还按着；
    /// 等完成页把窗口撑回来，「完成」正好长在静止不动的指针底下，那一次积压的按下就在它身上结了账。</para>
    /// <para>2.4.0 的修法是"让页脚待在同一格"；3.6.0 按用户那句「这些按钮太靠下了」把多余的高度
    /// 改到页脚<strong>下面</strong>，页脚因此不再固定在那一格 —— 所以这条改成量那条规矩本身：
    /// 换到完成页之后，内容区的下限（<c>MinHeight</c>）不许比「选定安装位置」那一页的正文更矮。
    /// headless 的窗口不会真的跟着内容改尺寸，只能按这条规矩自己的读数来量（与 2.4.0 那版同理）；
    /// 另一半由"进行中那一页没有任何按钮"那条守着 —— 松手那一带当时碰不到按钮。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheContentAreaNeverGetsShorterBetweenPages()
    {
        var lang = TestLang.Chinese();
        var window = new InstallerWindow(lang, _plan).Shown();

        ToLocationPage(window, lang);
        UncheckExtras(window);
        var installTop = TopOf(window, lang, "TEXT_BTN_INSTALL");

        Click(window, lang, "TEXT_BTN_INSTALL");
        WaitUntil(window, lang, "TEXT_SETUP_DONE_TITLE");

        Assert.True(window.HeldContentHeight >= installTop - 1,
            $"「安装」那一格在 y={installTop:0.#}，而换到完成页之后内容区的下限只有 "
            + $"{window.HeldContentHeight:0.#} —— 窗口缩了，用户点「安装」那一下松手就会落到窗口外面");
    }

    private static double TopOf(InstallerWindow window, LangCatalog lang, string labelKey)
    {
        UiWait.Pump(60);
        var button = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label(labelKey)));

        return button.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.Y;
    }

    // --------------------------------------------------------------- 小工具

    /// <summary>把两个会动开发机配置的开关关掉（关联与右键菜单）。</summary>
    private static void UncheckExtras(InstallerWindow window)
    {
        foreach (var row in VisualQuery.Of<SettingToggle>(window))
        {
            row.IsChecked = false;
        }
    }

    /// <summary>
    /// 走到「选定安装位置」那一页。2.1.0 起中间多了一页「为哪位用户安装」，
    /// 所以这里连点两下「下一步」—— 少一下就会停在范围页上，看起来像"位置那一页不见了"。
    /// </summary>
    private static void ToLocationPage(InstallerWindow window, LangCatalog lang)
    {
        Click(window, lang, "TEXT_BTN_NEXT");
        Click(window, lang, "TEXT_BTN_NEXT");
    }

    private static void Click(InstallerWindow window, LangCatalog lang, string labelKey)
    {
        var button = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label(labelKey)));

        Clicker.Invoke(button);
        UiWait.Pump(80);
    }

    /// <summary>
    /// 等向导自己走到完成页（而不是等某个目标文件出现）：文件在复制刚开始就被创建出来，
    /// 拿它当信号等于在测一个半成品。
    /// </summary>
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

    private InstallationService Service() => new(TestLang.Chinese());

    /// <summary>一个"够用像 exe"的源文件：安装器复制的是字节，不看内容。</summary>
    private string SourceExe()
    {
        var path = Path.Combine(_root, "Andzip-Setup-source.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);
        return path;
    }
}
