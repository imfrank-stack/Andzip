using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Andzip.Core.Archive;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>一次安装实际做了什么，用来在界面上如实报告（不写"成功"如果只做成一半）。</summary>
/// <param name="DesktopShortcut">桌面那一个快捷方式到底建了没有。为 false 时界面不能说"桌面已创建"。</param>
internal sealed record InstallOutcome(string ExecutablePath, IReadOnlyList<string> BlockedExtensions, bool DesktopShortcut);

/// <summary>
/// 「正常安装」这条路径：把绿色版那一个 exe 变成桌面上有图标、开始菜单里有项、
/// 控制面板里能卸载的程序。
/// <para><strong>装的是同一个单文件 exe</strong>，不是解出一个目录。绿色版本来就是一个文件，
/// 复制一份进 <c>%LOCALAPPDATA%\Programs\Andzip</c> 就够了 —— 再做一个"多文件安装版"
/// 只会让出包脚本要维护两套产物、两套体积数字。</para>
/// <para><strong>全程只写当前用户</strong>：程序文件在 LOCALAPPDATA、快捷方式在 APPDATA、
/// 卸载表在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall</c>。
/// 一处都不需要管理员，也不需要 UAC 弹窗。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallationService
{
    private const string Publisher = "imfrank-stack";

    private readonly LangCatalog _lang;

    /// <summary>
    /// 作用域 → 注册表根。默认按 <see cref="RegistryScope"/> 走（HKCU / HKLM 的 64 位视图）。
    /// <para>测试与 <c>/selftest</c> 把它换成"两个作用域都指 HKCU 下的探针键"，于是机器级那一条
    /// 往返（Program Files 换成临时目录）可以整条真跑一遍而不碰 <c>HKLM</c> —— 否则"装给所有用户"
    /// 这件事在这台非管理员的机器上连一次都跑不了，只能靠读代码相信它对了。</para>
    /// </summary>
    private readonly Func<InstallScope, RegistryKey> _root;

    public InstallationService(LangCatalog lang, Func<InstallScope, RegistryKey>? root = null)
    {
        _lang = lang;
        _root = root ?? RegistryScope.Open;
    }

    private RegistryKey Root(InstallPlan plan) => _root(plan.Scope);

    /// <summary>
    /// 按这台机器的真实目录算出该装到哪（桌面被 OneDrive 改指向时也跟着走）。
    /// </summary>
    /// <param name="scope">装给谁。<see cref="InstallScope.CurrentUser"/> 用
    /// <c>%LOCALAPPDATA%\Programs\Andzip</c>（不需要管理员）；
    /// <see cref="InstallScope.AllUsers"/> 用 <c>%ProgramFiles%\Andzip</c>、
    /// <c>%ProgramData%</c> 下那个所有人都看得见的开始菜单与公共桌面，卸载表写 <c>HKLM</c>。</param>
    /// <param name="installDirectory">用户在向导里选的位置；null 用该作用域的默认目录。</param>
    public InstallPlan CurrentPlan(InstallScope scope = InstallScope.CurrentUser, string? installDirectory = null)
    {
        var shortcut = _lang.Label("TEXT_INSTALLER_UNINSTALL_SHORTCUT");

        return scope == InstallScope.AllUsers
            ? InstallLayout.PlanForAllUsers(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Folder(Environment.SpecialFolder.CommonDesktopDirectory),
                installDirectory,
                shortcut)
            : InstallLayout.Plan(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                DesktopFolder(),
                installDirectory,
                shortcut);

        // 精简过的系统上已知文件夹可能取不到（返回空串），那时 null 比空串好：
        // 空串会拼出一个指向根目录的路径，而 null 是"这一项跳过"，界面会照实说。
        static string? Folder(Environment.SpecialFolder folder)
            => Environment.GetFolderPath(folder) is { Length: > 0 } path ? path : null;
    }

    /// <summary>
    /// 卸载用的落点：<strong>优先按卸载表里记着的那个安装位置</strong>。
    /// <para>装到自定义目录之后，卸载必须回到那个目录去删。只看默认路径的后果是"卸完了
    /// D:\Tools\Andzip 还留在那儿，而开始菜单和注册表里的东西没了" —— 半截卸载比不卸更难看。</para>
    /// </summary>
    /// <param name="uninstallKey">读哪一条卸载表。真机是默认那一条（两个根下同一个相对路径）；
    /// 测试与 <c>/selftest</c> 传探针键名，于是这一条可以在不碰开发机的前提下真跑。</param>
    public InstallPlan RecordedPlan(string uninstallKey = InstallLayout.UninstallKey)
    {
        var machine = InstalledCopy(InstallScope.AllUsers, uninstallKey);
        var user = InstalledCopy(InstallScope.CurrentUser, uninstallKey);

        // 一台机器上只该有一份（见 InstalledCopy），但"另一份还在"是这台机器当下的事实：
        // 卸载程序是被某个具体 exe 的 UninstallString 拉起来的 —— 那个 exe 在哪，卸的就是哪一份。
        var scope = InstallLayout.ScopeFor(
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty), machine?.Directory, user?.Directory);
        var recorded = scope == InstallScope.AllUsers ? machine : user;

        // 记着的位置已经不在盘上了（用户自己删了目录）也要照用：卸载要做的是
        // "把记着的那几处收干净"，指回默认目录反而会去删一个跟这次无关的位置。
        return CurrentPlan(scope, recorded?.Directory) with { UninstallKey = uninstallKey };
    }

    /// <summary>
    /// 这个作用域下<strong>卸载表记着</strong>的那一份；没装过返回 null。
    /// <para>判据是 <c>InstallLocation</c> 有值 —— 一条没有位置的卸载项没法照着它收回任何东西，
    /// 把它当"装过"会让向导去卸一个不存在的地方。</para>
    /// <para>存在的理由是"一台机器上只该有一份 Andzip"：装之前先去看<strong>另一个</strong>作用域
    /// （<see cref="InstallLayout.Opposite"/>），有就报出来并先卸掉，不然桌面上会有两个都叫
    /// Andzip 的图标、卸载表里两条都叫 Andzip 的项，而两套文件关联互相盖。</para>
    /// </summary>
    public InstalledCopy? InstalledCopy(InstallScope scope, string uninstallKey = InstallLayout.UninstallKey)
    {
        using var root = _root(scope);
        using var opened = root.OpenSubKey(uninstallKey);
        if (opened?.GetValue("InstallLocation") is not string { Length: > 0 } location)
        {
            return null;
        }

        return new InstalledCopy(scope, location, opened.GetValue("DisplayVersion") as string);
    }

    /// <summary>
    /// 装（或重装）。
    /// </summary>
    /// <param name="plan">落点。测试传的是临时目录 + 探针键名，真机传 <see cref="CurrentPlan"/>。</param>
    /// <param name="sourceExe">要被复制过去的那个 exe（通常是正在运行的安装器自己）。</param>
    /// <param name="progress">每一步开始前报一次。它在<strong>干活的那个线程上</strong>被调用，
    /// 所以传进来的必须是走 Dispatcher 的汇（见 <c>UiProgress</c>）—— 直接传
    /// <c>Progress&lt;T&gt;</c> 会在池线程上改控件，1.8.1 与 0.9.11 都是栽在这类事上。</param>
    public InstallOutcome Install(
        InstallPlan plan,
        string sourceExe,
        bool associate,
        bool contextMenu,
        IProgress<ProgressStep>? progress = null)
    {
        // 一共几步由勾选决定（没勾关联就不该有那一步），所以步数在这里算、百分比也由这里报，
        // 界面只管把收到的那一句写出来。
        var total = 3 + (associate ? 1 : 0) + (contextMenu ? 1 : 0);
        var done = 0;
        void Step(string key) => progress?.Report(new ProgressStep(key, done * 100 / total));

        Step("TEXT_SETUP_STEP_COPY");
        Directory.CreateDirectory(plan.InstallDirectory);

        if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(plan.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceExe, plan.ExecutablePath, overwrite: true);
        }

        File.WriteAllText(plan.UninstallScript, InstallLayout.UninstallScriptText, System.Text.Encoding.ASCII);

        // 安装目录里要有一个看得见、双击得动的卸载入口。卸载逻辑在 exe 里，
        // 所以这是一个 90 字节的批处理而不是第二份 20 MB 的拷贝（理由写在 InstallLayout 上）。
        // 它随目录一起被收回，卸载那边不需要单独记它一笔。

        done++;

        Step("TEXT_SETUP_STEP_LINKS");
        var description = _lang.Label("TEXT_INSTALLER_SHORTCUT_HINT");
        ShellLink.Create(plan.StartMenuShortcut, plan.ExecutablePath, null, description);

        // 卸载那一项也要在开始菜单里看得见 —— 用户找不到的功能等于没有（1.9.2 真机核对：
        // 「没有卸载 uninstall 和开始菜单」说的就是这件事，虽然注册表那条一直在）。
        ShellLink.Create(
            plan.StartMenuUninstallShortcut,
            plan.ExecutablePath,
            UninstallArgument,
            _lang.Label("TEXT_INSTALLER_UNINSTALL_SHORTCUT"));

        // 桌面那一个可能整个没有落点（DesktopFolder 取不到）—— 这不是"建好了"，
        // 所以把它作为一个事实带回去给界面说，而不是悄悄少做一件事。
        var desktopMade = false;
        if (plan.DesktopShortcut is { } desktop)
        {
            ShellLink.Create(desktop, plan.ExecutablePath, null, description);
            desktopMade = true;
        }

        done++;

        Step("TEXT_SETUP_STEP_REGISTRY");
        WriteUninstallEntry(plan);

        done++;

        var blocked = new List<string>();
        if (associate)
        {
            Step("TEXT_SETUP_STEP_ASSOC");

            // 这两个服务都接 _root：不接的话"作用域 → 注册表根"这条缝只到本类的键为止，
            // 装/卸往返的测试与 /selftest 就会去动这台机器上真的 Andzip.* 关联
            // （机器级那一份还会在没有管理员权限时直接抛 UnauthorizedAccessException）。
            var associations = new FileAssociationService(plan.Scope, _root);

            // 动词表按这台机器当下的设置算：双击那一行在菜单上叫什么，得跟「解压 → 双击压缩包文件时」
            // 说的是同一件事。提权那一份读到的是它自己那个用户的设置（可能是空的），
            // 那种情况下名字先按"打开"写，第一次通过关联被拉起来时 RepairCommands 会校正过来。
            var labels = ShellVerbLabels.Extraction(_lang, AppSettings.Load(AppSettings.DefaultFilePath));
            var captured = new List<AssociationOriginal>();

            foreach (var id in FileAssociationCatalog.InstallerDefaults)
            {
                if (FileAssociationCatalog.Find(id) is not { } association)
                {
                    continue;
                }

                var change = associations.Register(association, plan.ExecutablePath, labels);
                blocked.AddRange(change.BlockedExtensions);
                captured.AddRange(change.CapturedDefaults);
            }

            // 接管前的默认值必须落盘，否则卸载时不知道还原成什么 —— 只删值会把用户原来的
            // 「.zip 用 WinRAR 打开」一起带走。已经记过的不覆盖：重复登记时服务对
            // "本来就是我们"的扩展名返回空串，拿它盖掉第一次记下的真实原值就写坏了撤销路径。
            if (captured.Count > 0)
            {
                var fresh = AppSettings.Load(AppSettings.DefaultFilePath);
                var known = AssociationOriginals
                    .Decode(fresh.GetString(AssociationOriginals.SettingKey))
                    .ToDictionary(item => item.Extension, item => item.ProgId, StringComparer.OrdinalIgnoreCase);

                foreach (var item in captured.Where(item => !known.ContainsKey(item.Extension)))
                {
                    known[item.Extension] = item.ProgId;
                }

                fresh.SetString(
                    AssociationOriginals.SettingKey,
                    AssociationOriginals.Encode(known.Select(pair => new AssociationOriginal(pair.Key, pair.Value)).ToList()));
                fresh.Save(AppSettings.DefaultFilePath);
            }

            associations.NotifyChanged();
            done++;
        }

        if (contextMenu)
        {
            Step("TEXT_SETUP_STEP_MENU");
            var menu = new ContextMenuService(plan.Scope, _root);
            menu.Install(ShellVerbLabels.Compression(_lang), plan.ExecutablePath, forFiles: true, forFolders: true);
            menu.NotifyChanged();
        }

        return new InstallOutcome(plan.ExecutablePath, blocked, desktopMade);
    }

    /// <summary>
    /// 卸载：把 <see cref="Install"/> 做过的一件件收回来，最后删程序文件。
    /// <para><strong>关联与右键菜单不再按"当初是不是我装的"决定收不收</strong>：判据换成
    /// "这条登记指着的是不是正在被卸掉的那一个 exe"。按开关收会漏 —— 用户在
    /// 「设置 → 文件关联」里自己勾上的那几项，安装器记的开关仍然是关的，
    /// 于是卸完盘上没东西了，右键菜单与双击却还认得 Andzip（指向一个已经不存在的 exe）。
    /// 新判据也不会多删：别的软件的登记、另一份 Andzip 的登记，命令行里都不是这个路径。</para>
    /// </summary>
    public bool Uninstall(InstallPlan plan, IProgress<ProgressStep>? progress = null)
    {
        var uninstallShortcut = ReadUninstallShortcut(plan);

        var total = 5;
        var done = 0;
        void Step(string key) => progress?.Report(new ProgressStep(key, done * 100 / total));

        Step("TEXT_SETUP_STEP_UNLINKS");
        ShellLink.Delete(plan.StartMenuShortcut);

        // 按<strong>装的时候记下的那个名字</strong>删：卸载快捷方式的名字来自语言包，
        // 装完之后换过界面语言的话，按当下的语言算出来的名字删不到东西，开始菜单里会留一个死图标。
        ShellLink.Delete(uninstallShortcut ?? plan.StartMenuUninstallShortcut);

        if (plan.DesktopShortcut is { } desktop)
        {
            ShellLink.Delete(desktop);
        }

        done++;

        Step("TEXT_SETUP_STEP_UNREG");
        using (var root = Root(plan))
        {
            root.DeleteSubKeyTree(plan.UninstallKey, throwOnMissingSubKey: false);
        }

        done++;

        Step("TEXT_SETUP_STEP_UNASSOC");
        var settings = AppSettings.Load(AppSettings.DefaultFilePath);
        new FileAssociationService(plan.Scope, _root).RemoveOwnedBy(
            plan.ExecutablePath,
            AssociationOriginals.Decode(settings.GetString(AssociationOriginals.SettingKey)));

        done++;

        Step("TEXT_SETUP_STEP_UNMENU");
        var menu = new ContextMenuService(plan.Scope, _root);
        if (menu.Remove(plan.ExecutablePath) > 0)
        {
            menu.NotifyChanged();
        }

        done++;

        Step("TEXT_SETUP_STEP_UNFILES");
        return TryRemoveProgramFiles(plan);
    }

    /// <summary>
    /// 删掉安装目录里的程序文件。
    /// <para>从「控制面板 → 卸载」进来时，我们<strong>就是</strong>那个正在运行的 exe，
    /// 系统不允许删一个镜像还被映射着的文件。这里的处理是：让一个脱离本进程的
    /// <c>powershell</c> 等这个进程真的退出之后再删（见 <see cref="ScheduleDeleteOnExit"/>）。
    /// 界面会把这一句如实说出来，不假装当场删干净了。</para>
    /// <para><strong>两种异常都得接</strong>：占用的镜像在 Windows 上有时报"正在使用"
    /// （<see cref="IOException"/>），有时报"拒绝访问"（<see cref="UnauthorizedAccessException"/>）。
    /// 1.9.2 只接了前者，于是真机上从已安装的那一份里卸载 —— 恰好是最常走的那条路 ——
    /// 延迟删除根本没启动，程序文件留在盘上，界面上是一行英文异常原文。</para>
    /// </summary>
    private bool TryRemoveProgramFiles(InstallPlan plan)
    {
        try
        {
            if (Directory.Exists(plan.InstallDirectory))
            {
                Directory.Delete(plan.InstallDirectory, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            ScheduleDeleteOnExit(plan.InstallDirectory);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            ScheduleDeleteOnExit(plan.InstallDirectory);
            return false;
        }
    }

    /// <summary>
    /// 让一个脱离本进程的 <c>powershell</c> 等这个进程真的退出，然后把目录删掉。
    /// <para><strong>为什么不是"睡两秒再删"</strong>：1.9.2 那版是 <c>ping -n 3</c> 之后
    /// <c>rmdir</c>，可"我们退出了"与"两秒过去了"是两件事 —— 界面上那句"已安排在退出后删除"
    /// 说的是前者，做的却是后者。用户在完成页上多停几秒（读那两行字、切去开个文件管理器），
    /// rmdir 就已经在还锁着的文件上失败过了，目录永远留在盘上（2.0.0 真机核对：卸完之后
    /// <c>%LOCALAPPDATA%\Programs\Andzip\Andzip.exe</c> 还在）。</para>
    /// <para>现在按 <c>$pid</c> 等：<c>Wait-Process</c> 阻塞到那个进程真的没了再删，
    /// 并留 120 秒上限，免得一个卡死的进程把这条挂成永久。</para>
    /// </summary>
    private static void ScheduleDeleteOnExit(string directory)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = DeleteArguments(Environment.ProcessId, directory),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(start);
        }
        catch (Exception)
        {
            // 起不了这个辅助进程（没有 powershell、策略挡住）只是"目录留在那儿"：
            // 卸载的主体（快捷方式、注册表、关联、右键菜单）都已经收回去了，
            // 不该为这一点收尾工作把整次卸载报成失败
        }
    }

    /// <summary>
    /// 那条"等退出再删"的整串参数（<c>powershell.exe</c> 的那一边）。
    /// <para>路径是用户在向导里可以打的东西，所以它只能待在<strong>单引号字面量</strong>里，
    /// 并且把里面的单引号按 PowerShell 的规矩翻倍 —— 否则一个带 <c>'</c> 的目录名
    /// 就能把这条命令改成别的命令。进程号是 int，拼进去没有这个问题。</para>
    /// <para>这里交出去的是<strong>整串参数</strong>而不是命令本体：2.0.0 第一版在它前面留了一个
    /// <c>/c</c>（那是上一条 <c>cmd.exe</c> 写法剩下的），PowerShell 于是把 <c>/c</c> 当成要执行的
    /// 命令名，报一句"无法将/c项识别为cmdlet"就退了 —— 目录照样留在盘上，而界面上那句
    /// "已安排在退出后删除"说得毫无问题。所以断言要能看见开头。</para>
    /// <para><strong>删要重试，一次不够</strong>（3.7.0）：<c>Wait-Process</c> 回来之后进程是退了，
    /// 那个 exe 的镜像却可能还 mapped 一小会儿（杀软与索引器也会多拿一会儿）。原先这里只
    /// <c>Remove-Item -Recurse -Force -ErrorAction SilentlyContinue</c> 一次，而它按字母序先撞
    /// <c>Andzip.exe</c> —— 那一下失败被 SilentlyContinue 咽掉，后面的 <c>uninstall.bat</c> 却删掉了，
    /// 于是用户看到的就是"卸载不干净，exe 文件没被删掉"（真机量的：卸完之后目录里只剩
    /// <c>Andzip.exe</c> 一个文件，卸载表、快捷方式都没了）。现在最多试 200 次、每次隔 150 毫秒，
    /// 并且先点名下掉 exe 再收目录。</para>
    /// </summary>
    internal static string DeleteArguments(int processId, string directory)
    {
        var literal = directory.Replace("'", "''");
        var exe = Path.Combine(directory, InstallLayout.ExeName).Replace("'", "''");

        return $"-NoProfile -NonInteractive -Command \"Wait-Process -Id {processId} -Timeout 120 -ErrorAction SilentlyContinue; "
            + $"for ($i = 0; $i -lt 200; $i++) {{ if (-not (Test-Path -LiteralPath '{literal}')) {{ break }}; "
            + $"Remove-Item -LiteralPath '{exe}' -Force -ErrorAction SilentlyContinue; "
            + $"Remove-Item -LiteralPath '{literal}' -Recurse -Force -ErrorAction SilentlyContinue; "
            + $"Start-Sleep -Milliseconds 150 }}\"";
    }

    private void WriteUninstallEntry(InstallPlan plan)
    {
        using var root = Root(plan);
        using var key = root.CreateSubKey(plan.UninstallKey);

        key.SetValue("DisplayName", "Andzip");
        key.SetValue("DisplayVersion", DisplayVersion(plan.ExecutablePath));
        key.SetValue("Publisher", Publisher);

        // 机器级那一条写在 HKLM 下，看着不会混，但卸载时"该收哪一处"要靠它 + 运行路径一起判
        key.SetValue("AndzipScope", plan.Scope == InstallScope.AllUsers ? "Machine" : "User");
        key.SetValue("InstallLocation", plan.InstallDirectory);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

        // DisplayIcon 指回 exe 本身：控制面板那一列小图标因此与桌面上那个是同一张
        key.SetValue("DisplayIcon", plan.ExecutablePath);
        key.SetValue("UninstallString", $"\"{plan.ExecutablePath}\" {UninstallArgument}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // 只记"卸载快捷方式叫什么"这一件卸载时找不回来的事。当初勾没勾关联与右键菜单
        // 以前记在这里、卸载时照着收 —— 那是"卸载不该顺手删掉它没做过的事"的凭据，
        // 3.7.0 把它换成了更强的判据（这条登记指着的是不是正在被卸掉的那个 exe），
        // 于是那两条记号成了没人读的值：留着就是让下一个读注册表的人猜它是干什么的。
        key.SetValue(ValueUninstallShortcut, plan.StartMenuUninstallShortcut);

        var sizeKb = (long)new FileInfo(plan.ExecutablePath).Length / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
    }

    private const string ValueUninstallShortcut = "AndzipUninstallShortcut";

    /// <summary>卸载入口那一条命令行参数。开始菜单的快捷方式与注册表的卸载命令共用。</summary>
    private const string UninstallArgument = "/uninstall";

    /// <summary>
    /// 卸载表里显示的版本号。
    /// <para><strong>必须切掉加号后面那一段</strong>：那是 SourceLink 附的提交号，
    /// 直接写进注册表会让系统的「设置 → 应用」显示成
    /// <c>1.9.2+45.718056ece53be97a34004652df38b3af68954316</c>（1.9.2 真机核对量到的）。
    /// 标题栏那一处在 1.9.1 已经改成读 <c>AssemblyName.Version</c>，这一处读的是
    /// 复制过去那个 exe 的版本信息，所以得自己切。</para>
    /// </summary>
    private static string DisplayVersion(string executablePath)
        => CleanVersion(FileVersionInfo.GetVersionInfo(executablePath).ProductVersion);

    /// <summary>版本信息读不到时（源文件没有版本资源）退回自己这份程序集的版本。</summary>
    internal static string CleanVersion(string? productVersion)
    {
        var clean = productVersion?.Split('+', 2)[0].Trim();

        return string.IsNullOrWhiteSpace(clean)
            ? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0"
            : clean;
    }

    /// <summary>卸载表里记着的只有这一件：卸载快捷方式叫什么。名字来自语言包，换过界面语言就按当下的算不回来了。</summary>
    private string? ReadUninstallShortcut(InstallPlan plan)
    {
        using var root = Root(plan);
        using var key = root.OpenSubKey(plan.UninstallKey);
        return key?.GetValue(ValueUninstallShortcut) as string;
    }

    // ------------------------------------------------------ 桌面目录

    /// <summary>
    /// <c>FOLDERID_Desktop</c>。<strong>这个值必须从文档抄，不能凭记忆写</strong>：
    /// GUID 写错时 <c>SHGetKnownFolderPath</c> 只回一个 <c>0x80070002</c>，
    /// 我们把它当成"这台机器没有桌面目录"，于是桌面快捷方式<strong>静默地永远不建</strong> ——
    /// 1.8.4 之前正是这样，而全部单元测试都是绿的（它们传的是临时目录，压根不经过这一层）。
    /// 现在 <c>InstallerTests</c> 与 <c>/selftest</c> 都直接量这个函数。
    /// </summary>
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    /// <summary>
    /// 真实的桌面目录。<c>Environment.SpecialFolder.Desktop</c> 在 OneDrive 接管过的机器上
    /// 会指到没在桌面上显示的那个旧路径，所以走已知文件夹接口。
    /// <para>返回 null 只该发生在"这台机器真的取不到桌面"，此时<strong>界面必须说桌面那一个没建</strong>
    /// （见 <see cref="InstallOutcome.DesktopShortcut"/>）—— 静默少做一件事比明说更糟。</para>
    /// </summary>
    public static string? DesktopFolder()
    {
        var id = FolderIdDesktop;
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var memory) != 0 || memory == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(memory);
        }
        finally
        {
            CoTaskMemFree(memory);
        }
    }
}
