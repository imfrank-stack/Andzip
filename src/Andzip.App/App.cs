using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;

namespace Andzip.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());

        // 提示气泡也换成圆角 + 阴影的那层外壳（它没有弹窗那枚 Border，只能换模板）。
        Chrome.InstallBubbles(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 设置要先读：语言页存的那一项决定界面用什么语言，而语言包一旦建成就到处引用。
            var settings = AppSettings.Load(AppSettings.DefaultFilePath);

            // 主题要在建窗口之前定下来：建完再翻，用户会先看见一帧浅色首页再跳成深色。
            Design.Apply(SettingKeys.ParseThemeMode(settings.GetString(SettingKeys.ViewTheme)));

            // 「跟随系统」得能在运行中也跟着变（系统深浅是通过这个事件报上来的）。
            ActualThemeVariantChanged += (_, _) =>
            {
                if (Design.Mode == ThemeMode.System)
                {
                    Design.Apply(ThemeMode.System);
                }
            };

            // 「设置 → 高级」里那两项是进程级的，在这里一次性接上 —— 比让每个用到它的地方
            // 各自去读设置文件要难漏得多。
            ArchiveEngine.TempDirectory = settings.GetString(SettingKeys.TempDirectory);
            Session.Passwords.Enabled = settings.GetBool(SettingKeys.RememberPasswordsThisSession, fallback: true);

            // 口令的第二层（跨重启）。两个条件都要满足才生效：这台机器上有操作系统的凭据存储，
            // 并且用户在「设置 → 安全」里显式打开过。<strong>默认关</strong> —— 把口令留在机器上是
            // 一次要用户点头的交易，不该是装完就生效的默认值；而这一层永远不碰 settings.ini 里的口令。
            Session.Passwords.Persistent = PasswordVault.Open(AppSettings.DefaultFilePath);
            Session.Passwords.PersistPasswords = settings.GetBool(SettingKeys.StorePasswordsInVault);

            // 语言包：程序目录 langs/ 优先（便携覆盖），否则用内嵌资源（单文件绿色版）。
            var lang = LangLoader.CreateCatalog(
                typeof(App).Assembly, AppContext.BaseDirectory, PreferredLanguage(settings));
            var target = desktop.Args?.FirstOrDefault(IsExistingPath);

            if (target is not null)
            {
                RepairStaleShellEntries(lang, settings);
            }

            // 不建主窗口的两条路径：右键动词带来的 /task=，以及双击 + 「解压 → 双击 = 直接解压」。
            // 生命周期要改成显式关闭，否则没有主窗口会让进程立刻退出。
            if (target is not null && TryResolveTask(desktop.Args, settings, out var task))
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = ShellTaskRunner.RunAsync(lang, settings, task, target, desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // 同一个二进制，两种身份：文件名是 Andzip-Setup* （用户从资源管理器双击的那一个），
            // 或者带了 /uninstall（控制面板与开始菜单里那条卸载命令），就开向导而不是主窗口。
            // 带 /uninstall 进来时开的是卸载那一套页 —— 让人家双击了安装向导再自己找红色按钮，
            // 是 1.9.2 真机核对里"没有卸载 uninstall"那句抱怨的一半原因。
            // 安装这件事只在 Windows 上存在，所以先问一句操作系统 —— 项目还要在 Linux 上构建。
            if (OperatingSystem.IsWindows()
                && (InstallLayout.IsInstallerFile(Environment.ProcessPath ?? string.Empty)
                    || (desktop.Args?.Any(arg => string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(arg, "/install", StringComparison.OrdinalIgnoreCase)) ?? false)))
            {
                var args = desktop.Args ?? [];
                bool Has(string flag) => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

                desktop.MainWindow = new InstallerWindow(
                    lang,
                    startsAsUninstaller: Has("/uninstall"),
                    // 提权之后那一个新的实例带着这两个开关之一：范围在那一步已经定了，
                    // 而且这一份有了管理员权限，再问一遍"装给谁"是白问。两个都要 ——
                    // "只给自己装，但要先卸掉 Program Files 里那一份"也会提权，
                    // 那时范围仍然是当前用户。
                    preselectedScope: Has(InstallerWindow.ArgumentAllUsers) ? InstallScope.AllUsers
                        : Has(InstallerWindow.ArgumentCurrentUser) ? InstallScope.CurrentUser
                        : null,
                    // 卸另一份这件事跨不过进程：不提权就删不动，提了权回来又不能让它忘了要删
                    replaceOther: Has(InstallerWindow.ArgumentReplaceOther));
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // 用户自己调的那套图标配色：文件在就接回来。这一步必须在建窗口之前 ——
            // 窗口那一张是在构造时从 Assets 取的，晚一步主窗口就穿着旧衣服开出来。
            if (OperatingSystem.IsWindows())
            {
                new IconStudio().RestoreFromSettings(IconPreferences.Load(settings));
            }

            var window = new MainWindow(lang, settings);            desktop.MainWindow = window;
            // 命令行带压缩包路径时直接打开：文件关联与「发送到 Andzip」都依赖这条入口。
            if (target is not null)
            {
                _ = window.OpenArchive(target);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? PreferredLanguage(AppSettings settings)
    {
        var stored = settings.GetString(SettingKeys.Language);
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    /// <summary>
    /// 命令行里的目标路径。目录也算 —— 右键「压缩到」既给文件也给文件夹，
    /// 而 <c>File.Exists</c> 对文件夹返回 false，会把这个入口整个漏掉。
    /// </summary>
    private static bool IsExistingPath(string argument)
        => !argument.StartsWith('-') && (File.Exists(argument) || Directory.Exists(argument));

    /// <summary>
    /// 这次启动要不要走「没有主窗口」的后台路径，以及做哪件事。
    /// <para>
    /// 右键动词在命令行里写明了 <c>/task=</c>，直接照办；双击没有开关，做什么由
    /// 「解压 → 双击压缩包文件时」决定 —— 行为放在设置里而不是注册表的默认动词里，
    /// 改设置才不用重写所有 ProgID。
    /// </para>
    /// <para><c>open</c> 是反例：它要的正是主窗口，交回给调用方去建。</para>
    /// <para><strong>为什么不是 private</strong>：「双击那一档就是智能解压」这句话的全部内容，
    /// 是"双击解析出来的任务与右键动词解析出来的是同一个"。它只有在这一处汇合点才测得到 ——
    /// 界面测试碰不到启动分发，内核测试又看不见设置。
    /// 见 <c>UiFeatureWiringTests.TheDoubleClickChoiceResolvesToTheSameTaskAsTheVerb</c>。</para>
    /// </summary>
    internal static bool TryResolveTask(IEnumerable<string>? arguments, AppSettings settings, out ShellTask task)
    {
        if (ShellTasks.TryParse(arguments, out task))
        {
            return task != ShellTask.Open;
        }

        if (SettingKeys.ShouldExtractDirectly(settings.GetString(SettingKeys.DoubleClickAction)))
        {
            // 双击走解压，落点是**就地铺开**（内容直接出现在压缩包旁边）。
            // 与右键那条 <c>/task=extract</c> 的差别就在落点上：那条按包里的形状决定要不要套一层
            // 同名袋子（0.10.0 三条并一条时用户要的），而这一项的名字承诺的是"当前文件夹"。
            task = ShellTask.ExtractInPlace;
            return true;
        }

        task = default;
        return false;
    }

    /// <summary>
    /// 把注册表里指向旧 exe 的命令行、以及缺条目的右键动词校正到当前这个版本。
    /// <para>
    /// 绿色版的文件名带版本号，升级之后注册表里那条命令还指着上一个 exe，而那个老二进制不认
    /// 新行为 —— 用户看到的就是「关联做了、设置也改了，双击还是老样子」。启动时顺手把自己
    /// 命名空间下的键校正过来，就不必每次升级都重做一遍关联。
    /// </para>
    /// <para>只在真的通过关联被拉起来时做（否则首次运行会平白写一次注册表），且只动 <c>Andzip.*</c>。</para>
    /// <para>解压动词按「设置 → 上下文菜单」的选择补写：升级后新认出去的格式也该有右键菜单，
    /// 但用户明确关掉过的那一项不能被自愈偷偷加回来。压缩动词只补齐<strong>已经装了</strong>的范围，
    /// 没勾过的用户不会某天突然发现菜单里多了三项。</para>
    /// <para><strong>广播由这两个调用自己决定</strong>（值没变就不写、没写就不发 <c>SHChangeNotify</c>）：
    /// 这条路径每次双击/右键启动都会跑，而广播一次就是屏幕上所有图标闪一下 ——
    /// 早先把"重写了几个"当成"改了几个"，于是每一次解压或压缩都闪一次（3.2.0 报的）。</para>
    /// </summary>
    private static void RepairStaleShellEntries(LangCatalog lang, AppSettings settings)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return;
        }

        var executable = Environment.ProcessPath;

        try
        {
            var service = new FileAssociationService();
            service.RepairCommands(executable, ShellVerbLabels.Extraction(lang, settings));
        }
        catch (Exception)
        {
            // 修不动就算了：命令行本身仍然可用，不该因为校正注册表失败就拒绝解压。
        }

        try
        {
            var contextMenu = new ContextMenuService();
            contextMenu.RepairVerbs(executable, ShellVerbLabels.Compression(lang));
        }
        catch (Exception)
        {
            // 同上：右键压缩不是用户这一步要的结果，缺条目下次再补。
        }
    }
}
