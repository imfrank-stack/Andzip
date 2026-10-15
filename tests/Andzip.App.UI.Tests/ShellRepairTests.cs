using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.Core.Installation;
using Microsoft.Win32;

namespace Andzip.App.UiTests;

/// <summary>
/// 「启动时顺手校正关联」这条自愈路径：它每次通过关联启动都要跑一遍，
/// 所以它<strong>动了什么、有没有让 shell 重读</strong>就成了用户能看见的事。
/// <para>用户 2026-10-15 报的现象是「每次解压和压缩，屏幕上所有图标都闪一次」。链路是：
/// 这条路径把每一个 <c>Andzip.*</c> ProgID 里的值<strong>原样重写</strong>一遍（数出来的却是"我修了 9 个"），
/// 于是每次都发一次全局 <c>SHCNE_ASSOCCHANGED</c> —— shell 丢掉关联与图标缓存重读，图标整片闪一下。
/// 真机上量的：一次这样的启动把 <c>Andzip.zip</c> 与 <c>.zip</c> 两个键都写了一遍，值一个字都没变，
/// 紧接着 <c>iconcache_idx.db</c> 被重写。</para>
/// <para>所以这里量的是<strong>返回值与广播的对应关系</strong>，不是"跑完没抛异常"：
/// 已经对的东西再校正一次必须<strong>一个都不算改，也一声都不广播</strong>；
/// 真的改到了（老 exe 路径、缺的动词）必须算上并且广播一次。
/// 广播被注入成计数器 —— 它本身就是这条用例要钉住的东西，而且真广播会刷开发机的桌面图标。</para>
/// <para>注册表全部落在自己的探针子树里（<c>HKCU\Software\Andzip.Tests\ShellRepair</c>），
/// 不碰这台机器上真正的 <c>Andzip.*</c> 关联。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class ShellRepairTests : IDisposable
{
    private const string ProbeRoot = @"Software\Andzip.Tests\ShellRepair";
    private const string ProbeProgId = "Andzip.ProbeTest.zip";
    private const string Exe = @"D:\probe\Andzip-v3.2.1-win-x64.exe";

    private int _notified;

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ProbeRoot, throwOnMissingSubKey: false);
        GC.SuppressFinalize(this);
    }

    /// <summary>一个指向探针子树的关联服务，广播记在计数器上。</summary>
    private FileAssociationService Service()
        => new(InstallScope.CurrentUser, _ => Registry.CurrentUser.CreateSubKey(ProbeRoot)!, () => _notified++);

    private ContextMenuService Menu()
        => new(InstallScope.CurrentUser, _ => Registry.CurrentUser.CreateSubKey(ProbeRoot)!, () => _notified++);

    /// <summary>探针根下建一个装好的 ProgID（<c>shell</c> 在，命令指向别的 exe）。</summary>
    private static void MakeProgId(string openCommand)
    {
        using var root = Registry.CurrentUser.CreateSubKey(ProbeRoot);
        using var progId = root!.CreateSubKey(@"Software\Classes\" + ProbeProgId);
        using var shell = progId!.CreateSubKey("shell");
        shell!.SetValue(string.Empty, "open");
        using var open = shell.CreateSubKey("open");
        open!.SetValue(string.Empty, "打开");
        using var command = open.CreateSubKey("command");
        command!.SetValue(string.Empty, openCommand);
    }

    private static FileAssociationService.ExtractionVerbLabels Labels() => new("打开", "解压", "解压并删除");

    /// <summary>
    /// 这一条就是用户的 bug：**已经把命令行指对了，再启动一次必须什么都不做**。
    /// <para>第一次校正真的改了东西（老路径 → 新路径），所以它该算 1 并广播 1 次；
    /// 第二次照旧校正，值全都一样 —— 一个都不算改，**一次都不许广播**。</para>
    /// </summary>
    [Fact]
    public void ASteadyLaunchChangesNothingAndStaysQuiet()
    {
        MakeProgId(@"""D:\old\Andzip-v3.1.1-win-x64.exe"" ""%1""");
        var service = Service();

        var first = service.RepairCommands(Exe, Labels());
        Assert.Equal(1, first);
        Assert.Equal(1, _notified);

        var second = service.RepairCommands(Exe, Labels());

        Assert.Equal(0, second);
        Assert.Equal(1, _notified);
    }

    /// <summary>
    /// 反面：值真的不对时必须算上并广播 —— 否则"安静"就成了"这条自愈根本没在跑"，
    /// 而升级之后注册表还指着老 exe 这件事会悄悄回到用户面前。
    /// </summary>
    [Fact]
    public void ACommandPointingAtAnOlderExeIsRepairedAndAnnounced()
    {
        MakeProgId(@"""D:\old\Andzip-v3.1.1-win-x64.exe"" ""%1""");

        Assert.Equal(1, Service().RepairCommands(Exe, Labels()));
        Assert.Equal(1, _notified);

        using var root = Registry.CurrentUser.CreateSubKey(ProbeRoot);
        var written = root!
            .OpenSubKey($@"Software\Classes\{ProbeProgId}\shell\open\command")!
            .GetValue(string.Empty) as string;

        Assert.Equal(@"""D:\probe\Andzip-v3.2.1-win-x64.exe"" ""%1""", written);
    }

    /// <summary>缺的动词要补上（这是这条自愈存在的理由），补上也只算一次改动、一次广播。</summary>
    [Fact]
    public void AMissingVerbIsAddedOnceAndCountedOnce()
    {
        MakeProgId(@"""D:\probe\Andzip-v3.2.1-win-x64.exe"" ""%1""");

        // 先把全套动词写对（这一步会算改动），再删掉一条 —— 于是"缺一条"成为唯一的不一样。
        var service = Service();
        service.RepairCommands(Exe, Labels());
        _notified = 0;

        using (var root = Registry.CurrentUser.CreateSubKey(ProbeRoot))
        {
            root!.DeleteSubKeyTree($@"Software\Classes\{ProbeProgId}\shell\extract", throwOnMissingSubKey: false);
        }

        Assert.Equal(1, service.RepairCommands(Exe, Labels()));
        Assert.Equal(1, _notified);
    }

    /// <summary>
    /// 右键动词这一侧的形状与关联那一侧完全一样，所以同样钉一遍：
    /// 装齐了的范围再补一次是 0 且不广播；少一条才是 1 且广播一次。
    /// </summary>
    [Fact]
    public void TheContextMenuRepairAlsoCountsOnlyRealChanges()
    {
        var verbs = ShellVerbLabels.Compression(TestLang.Chinese());
        var menu = Menu();

        // 装到"所有文件"这一个范围（文件夹范围不装），并用同一个 exe 路径 —— 这是装好之后的样子。
        menu.Install(verbs, Exe, forFiles: true, forFolders: false);
        _notified = 0;

        Assert.Equal(0, menu.RepairVerbs(Exe, verbs));
        Assert.Equal(0, _notified);

        // 少一条动词：新版本加了动词、而老用户注册表里没有，正是这条自愈要补的形状。
        using (var root = Registry.CurrentUser.CreateSubKey(ProbeRoot))
        {
            root!.DeleteSubKeyTree(@"Software\Classes\*\shell\Andzip.compress-dialog", throwOnMissingSubKey: false);
        }

        Assert.Equal(1, menu.RepairVerbs(Exe, verbs));
        Assert.Equal(1, _notified);
    }

    /// <summary>
    /// 一个范围都没装时不许动它（没勾过的用户不该被塞进菜单），也不该广播 ——
    /// 这条与"装好了就安静"合起来，才是"广播只在真的改了东西时出现"的完整边界。
    /// </summary>
    [Fact]
    public void AnUninstalledScopeIsLeftAloneWithoutABroadcast()
    {
        using (Registry.CurrentUser.CreateSubKey(ProbeRoot)) { }

        var verbs = ShellVerbLabels.Compression(TestLang.Chinese());

        Assert.Equal(0, Menu().RepairVerbs(Exe, verbs));
        Assert.Equal(0, _notified);
    }
}
