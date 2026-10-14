using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Win32;

namespace Andzip.App.Views;

/// <summary>
/// 安装向导 / 卸载向导：把绿色版那一个 exe 装成"正常安装"的样子，或者把它收回去。
/// <para><strong>为什么要分成几页</strong>：1.9.2 及以前这是一整屏（一个路径 + 两个开关 +
/// 安装/卸载两颗按钮），点一下就动。用户的说法是"这个安装流程不对，应该是这样的"，
/// 附了两张 Inno Setup 的截图 —— 要的是<strong>先看要做什么、再决定、再看它做</strong>：
/// 欢迎 → 为哪位用户安装 → 选定安装位置（带所需空间 / 可用空间）→ 正在安装（分步进度）→ 完成。
/// 卸载也有自己的确认页与进度页，标题跟着变成「卸载 Andzip」。</para>
/// <para><strong>同一个二进制，两种身份</strong>：判据是它自己的文件名 —— 叫
/// <c>Andzip-Setup-*.exe</c> 时开安装向导，带 <c>/uninstall</c> 时开卸载向导，
/// 叫 <c>Andzip.exe</c> 时开主窗口。好处是安装器与应用<strong>不可能版本不一致</strong>
/// （出包只是复制改名一份文件），卸载表里那条也不会指向一个特制的壳。
/// 代价是不出 <c>uninstall.exe</c> 那一个文件：它得是同一份 20 MB 字节的第二份拷贝，
/// 而"卸载"这件事由 <c>Andzip.exe /uninstall</c> 做，开始菜单里那一项就是它。</para>
/// <para><strong>两种作用域，一次问清</strong>（2.1.0）：默认「仅为我自己」—— 关联、右键动词、
/// 设置文件、卸载表全在当前用户名下，一处都不需要管理员；「为所有用户」走 <c>Program Files</c>
/// 与 <c>HKLM</c>，这是这台软件唯一一处需要管理员的动作，判据不是"这个账户是不是管理员"而是
/// <c>Elevation.Needed</c>（那个位置现在写不写得进去），提不到就停在原地说明白，
/// <strong>不悄悄退回只给自己装</strong>。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerWindow : Window
{
    /// <summary>
    /// 走到哪一页。安装：欢迎 → <strong>安装范围</strong> → 位置 → 进行中 → 完成；
    /// 卸载：确认 → 进行中 → 完成。
    /// </summary>
    private enum WizardPage
    {
        Welcome,
        Scope,
        Location,
        Working,
        Done,
        ConfirmUninstall,
    }

    private readonly LangCatalog _lang;
    private readonly InstallationService _service;

    /// <summary>测试传进来的落点（临时目录 + 探针注册表键）。为 null 时按这台机器算。</summary>
    private readonly InstallPlan? _given;

    /// <summary>
    /// 要管理员时怎么提权。测试换成假的：真的 UAC 框在这里按不了，也不该由测试去按。
    /// <para>返回 true 表示"已经有一个提了权的实例接手了"，这一份就该收窗退出 ——
    /// 它没有权限，留着只会做半截安装。</para>
    /// </summary>
    private readonly Func<InstallScope, bool> _elevate;

    private InstallPlan _plan;
    private bool _uninstaller;
    private bool _busy;

    /// <summary>页与页之间不至于跳得太厉害；也不写死高度 —— 1.9.2 那次写死过，把内容切掉了一半。</summary>
    private readonly ContentControl _host = new() { MinHeight = 200 };
    private readonly StackPanel _footer = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = Design.Space.X3,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, Design.Space.X4, 0, 0),
    };

    private readonly TextBox _location = new();
    private readonly Button _browse = new() { MinWidth = 88 };
    private readonly TextBlock _spaceRequired = new();
    private readonly TextBlock _spaceAvailable = new();
    private readonly TextBlock _locationError = new() { IsVisible = false };
    private readonly TextBlock _welcomeState = new();
    private readonly TextBlock _workingHeading = new();
    private readonly TextBlock _workingStep = new();
    private readonly TextBlock _doneHeading = new();
    private readonly TextBlock _doneText = new();
    private readonly TextBlock _confirmText = new();
    private readonly TextBlock _uninstallHint = new();
    private readonly RadioButton _forUser = new();
    private readonly RadioButton _forAll = new();
    private readonly TextBlock _scopeError = new() { IsVisible = false };
    private readonly CheckBox _runAfter = new();
    private readonly SettingToggle _associate;
    private readonly SettingToggle _contextMenu;
    private readonly ProgressBar _bar = new()
    {
        Minimum = 0,
        Maximum = 100,
        Height = 8,
        CornerRadius = Design.Radius.Pill,
    };

    /// <summary>所有页面都挂在同一块面板上，翻页只是改可见性（见 <see cref="GoTo"/>）。</summary>
    private readonly Panel _stack = new();
    private readonly Dictionary<WizardPage, Control> _pages = new();

    /// <summary>完成页要照实说的那一句（装好了 / 桌面那个跳过了 / 卸了但文件留到退出后删）。</summary>
    private string _result = string.Empty;

    /// <param name="plan">落点。默认按这台机器的真实目录算；测试传一份临时目录 + 探针注册表键，
    /// 于是"点安装真的会跑一遍"这件事可以在不碰开发机配置的前提下验证。</param>
    /// <param name="startsAsUninstaller">带 <c>/uninstall</c> 进来时开卸载向导 ——
    /// 控制面板里那条卸载命令与开始菜单里那一项走的都是这一条。</param>
    /// <param name="preselectedScope">提权之后那一个新的实例带着 <c>/all-users</c> 进来：
    /// 范围在那一步已经定了（而且这一份有权限了），所以直接从「选定安装位置」那一页开始，
    /// 不再问第二遍"装给谁"。</param>
    /// <param name="elevate">要管理员时怎么提权（默认以管理员身份重启自己）。测试传假的。</param>
    public InstallerWindow(
        LangCatalog lang,
        InstallPlan? plan = null,
        bool startsAsUninstaller = false,
        InstallScope? preselectedScope = null,
        Func<InstallScope, bool>? elevate = null)
        : this(lang, plan, null, startsAsUninstaller, preselectedScope, elevate)
    {
    }

    /// <param name="root">作用域 → 注册表根。<strong>只有测试用</strong>：传一个"两个作用域都指 HKCU
    /// 探针键"的函数，机器级那一条整条往返就能在临时目录上真跑一遍（这台机器上的账户不是管理员，
    /// 真的 <c>HKLM</c> 写不进去，那条路否则永远只能靠读代码相信它对了）。</param>
    internal InstallerWindow(
        LangCatalog lang,
        InstallPlan? plan,
        Func<InstallScope, RegistryKey>? registryRoot,
        bool startsAsUninstaller,
        InstallScope? preselectedScope,
        Func<InstallScope, bool>? elevate)
    {
        _lang = lang;
        _service = registryRoot is null ? new InstallationService(lang) : new InstallationService(lang, registryRoot);
        _given = plan;
        _uninstaller = startsAsUninstaller;
        _elevate = elevate ?? (scope => Elevation.TryRelaunch(
            _uninstaller ? "/uninstall" : $"/install {ArgumentAllUsers}"));

        _plan = plan ?? (startsAsUninstaller ? _service.RecordedPlan() : _service.CurrentPlan());

        if (!startsAsUninstaller && preselectedScope is { } chosen)
        {
            _plan = PlanForScope(chosen);
        }

        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _location.Text = _plan.InstallDirectory;
        _location.Field();
        _location.TextChanged += (_, _) => RefreshSpace();

        _browse.Content = lang.Label("TEXT_SETUP_CHANGE");
        _browse.Ghost();
        _browse.Click += async (_, _) => await BrowseAsync();

        _associate = SettingRow.Toggle(lang, "TEXT_INSTALLER_ASSOCIATE", "TEXT_INSTALLER_ASSOCIATE_HINT", () => { });
        _contextMenu = SettingRow.Toggle(lang, "TEXT_INSTALLER_CONTEXT", "TEXT_INSTALLER_CONTEXT_HINT", () => { });

        // 已经装过一次就不再把勾选框默认打开：那时点「安装」是要重装/补关联，
        // 悄悄再改一遍他的默认程序设置是意外后果。
        // <strong>这一句不能写成"看 IsChecked 是不是 null"</strong>：ToggleButton 的初值是 false，
        // 那样的判断永远不成立，于是新装的用户两个开关都关着（2.0.0 真机核对：屏幕上两个开关是灰的，
        // 而同一扇窗口上一页明明写着"还没有安装"）。
        _associate.IsChecked = !Installed();
        _contextMenu.IsChecked = !Installed();

        _runAfter.Content = lang.Label("TEXT_SETUP_RUN_AFTER");
        _runAfter.IsChecked = true;
        _runAfter.FontSize = Design.Type.Body;

        // 进行中关不掉：复制进行到一半被强行终止会留下一个装了一半的目录，
        // 而这里没有"回滚"可做（我们只会复制一个文件，不会先备份旧的）。
        Closing += (_, e) => e.Cancel = _busy;

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_host, 0);
        Grid.SetRow(_footer, 1);
        root.Children.Add(_host);
        root.Children.Add(_footer);

        _host.Content = _stack;
        this.Chromeless(lang, root);

        GoTo(_uninstaller ? WizardPage.ConfirmUninstall
            : preselectedScope is null ? WizardPage.Welcome
            : WizardPage.Location);
    }

    // -------------------------------------------------------------- 页与页脚

    /// <summary>
    /// 翻到某一页。
    /// <para><strong>页面只在第一次走到时建，之后靠 <c>IsVisible</c> 切</strong>：
    /// 每翻一页就重建一遍内容的写法看着省事先，其实会崩 —— 路径框、两个开关这些控件是字段，
    /// 重建时要把它们从旧父节点摘下来挂到新的那一个上，而 <c>ContentControl</c> 换内容时
    /// 旧的子树还挂着视觉父节点（要等一次布局才清），于是"上一步 → 下一步"直接抛
    /// "The control TextBox already has a visual parent Grid"。2.0.0 第一次走到这条路上就撞上了。</para>
    /// </summary>
    private void GoTo(WizardPage page)
    {
        Title = _uninstaller ? _lang.Label("TEXT_TITLE_UNINSTALLER") : _lang.Label("TEXT_TITLE_INSTALLER");

        if (!_pages.TryGetValue(page, out var control))
        {
            control = Build(page);
            _pages[page] = control;
            _stack.Children.Add(control);
        }

        foreach (var (which, built) in _pages)
        {
            built.IsVisible = which == page;
        }

        Refresh(page);

        _footer.Children.Clear();
        foreach (var button in Buttons(page))
        {
            _footer.Children.Add(button);
        }
    }

    /// <summary>提权重启时带上这个开关：那一份实例据此跳过「安装范围」那一页。</summary>
    internal const string ArgumentAllUsers = "/all-users";

    private Control Build(WizardPage page) => page switch
    {
        WizardPage.Welcome => Welcome(),
        WizardPage.Scope => ScopePage(),
        WizardPage.Location => Location(),
        WizardPage.Working => Working(),
        WizardPage.Done => Done(),
        _ => ConfirmUninstall(),
    };

    /// <summary>页面上跟着状态变的那几行字（装没装过、正在做哪一步、结果是什么）。</summary>
    private void Refresh(WizardPage page)
    {
        switch (page)
        {
            case WizardPage.Welcome:
                _welcomeState.Text = Installed()
                    ? _lang.Format("TEXT_INSTALLER_ALREADY", ("dir", _plan.ExecutablePath))
                    : _lang.Label("TEXT_INSTALLER_NOT_YET");
                break;

            case WizardPage.Location:
                RefreshSpace();
                break;

            case WizardPage.Working:
                _workingHeading.Text = _lang.Label(
                    _uninstaller ? "TEXT_SETUP_UNINSTALL_WORKING_TITLE" : "TEXT_SETUP_WORKING_TITLE");
                break;

            case WizardPage.Done:
                _doneHeading.Text = _lang.Label(
                    _uninstaller ? "TEXT_SETUP_UNINSTALL_DONE_TITLE" : "TEXT_SETUP_DONE_TITLE");
                _doneText.Text = _result;

                // 卸载那一页不给"完成后运行"这一项，也不提示去哪儿卸载 —— 刚刚卸完。
                // 留一颗按了什么都不发生的勾选框，比少一颗更糟（规范 §5.7 同一条理由）。
                _uninstallHint.IsVisible = !_uninstaller;
                _runAfter.IsVisible = !_uninstaller;
                break;

            case WizardPage.ConfirmUninstall:
                _confirmText.Text = _lang.Format("TEXT_SETUP_CONFIRM_BODY", ("dir", _plan.InstallDirectory));
                break;
        }
    }

    private IEnumerable<Control> Buttons(WizardPage page)
    {
        switch (page)
        {
            case WizardPage.Welcome:
                if (Installed())
                {
                    yield return Push("TEXT_BTN_UNINSTALL", EnterUninstall, danger: true);
                }

                yield return Cancel();
                yield return Push("TEXT_BTN_NEXT", () => GoTo(WizardPage.Scope), accent: true, isDefault: true);
                break;

            case WizardPage.Scope:
                yield return Push("TEXT_BTN_BACK", () => GoTo(WizardPage.Welcome));
                yield return Cancel();
                yield return Push("TEXT_BTN_NEXT", ContinueFromScope, accent: true, isDefault: true);
                break;

            case WizardPage.Location:
                yield return Push("TEXT_BTN_BACK", () => GoTo(WizardPage.Scope));
                yield return Cancel();
                yield return Push("TEXT_BTN_INSTALL", Install, accent: true, isDefault: true);
                break;

            case WizardPage.ConfirmUninstall:
                yield return Cancel();
                yield return Push("TEXT_BTN_UNINSTALL", Uninstall, danger: true, isDefault: true);
                break;

            case WizardPage.Done:
                yield return Push("TEXT_BTN_FINISH", Finish, accent: true, isDefault: true);
                break;

            // 进行中不放按钮：这一页没有任何"当时能停"的动作可做（复制一个文件停不下来），
            // 放一颗点了没用的按钮比不放更让人犹豫。
            case WizardPage.Working:
                break;
        }
    }

    private Control Cancel() => Push("TEXT_BTN_CANCEL", Close, isCancel: true);

    private Control Push(string labelKey, Action onClick, bool accent = false, bool danger = false, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = _lang.Label(labelKey),
            MinWidth = 96,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };

        (accent ? button.Accent() : danger ? button.Danger() : button.Ghost())
            .Click += (_, _) => onClick();

        return button;
    }

    // -------------------------------------------------------------- 各页内容

    private Control Welcome()
    {
        var body = Stack();
        body.Children.Add(Heading("TEXT_SETUP_WELCOME_TITLE"));
        body.Children.Add(Paragraph("TEXT_SETUP_WELCOME_BODY"));

        _welcomeState.Foreground = Design.Ink.Secondary;
        _welcomeState.FontSize = Design.Type.Body;
        _welcomeState.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_welcomeState);

        return body;
    }

    /// <summary>
    /// 「为哪位用户安装」那一页。默认是当前用户（不需要管理员），
    /// 选「所有用户」就要过一次 UAC —— 这是这台软件唯一一处需要管理员的动作。
    /// </summary>
    private Control ScopePage()
    {
        var body = Stack();
        body.Children.Add(Heading("TEXT_SETUP_SCOPE_TITLE"));
        body.Children.Add(Paragraph("TEXT_SETUP_SCOPE_HINT"));

        _forUser.Content = _lang.Label("TEXT_SETUP_SCOPE_USER");
        _forAll.Content = _lang.Label("TEXT_SETUP_SCOPE_ALL");
        _forUser.IsChecked = _plan.Scope == InstallScope.CurrentUser;
        _forAll.IsChecked = _plan.Scope == InstallScope.AllUsers;
        _forUser.FontSize = Design.Type.Body;
        _forAll.FontSize = Design.Type.Body;

        // 两枚单选钮靠同一个父面板自动成组（Avalonia 按父级分组，不写 GroupName）
        var options = new StackPanel { Spacing = Design.Space.X2 };
        options.Children.Add(_forUser);
        options.Children.Add(_forAll);
        body.Children.Add(options);

        body.Children.Add(Text("TEXT_SETUP_SCOPE_NOTE", Design.Type.Caption, secondary: true));

        _scopeError.Foreground = Design.Semantic.Danger;
        _scopeError.FontSize = Design.Type.Caption;
        _scopeError.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_scopeError);

        return body;
    }

    /// <summary>
    /// 范围定了之后的那一步：先看这个作用域落哪儿写不写得进去，写不进去就提权，
    /// 提不到就停在原地说明白。
    /// <para>判据用<strong>可写性探针</strong>而不是"这个账户是不是管理员"：前者与安装真正会失败的
    /// 条件是同一件事，后者还要引 <c>WindowsIdentity</c>（非 Windows 目标上没有），
    /// 而且两者迟早会给出不同答案 —— 比如管理员账户被策略挡在 Program Files 外面。</para>
    /// </summary>
    private void ContinueFromScope()
    {
        var scope = _forAll.IsChecked == true ? InstallScope.AllUsers : InstallScope.CurrentUser;
        var chosen = PlanForScope(scope);

        if (Elevation.Needed(chosen))
        {
            if (_elevate(scope))
            {
                Close();
                return;
            }

            _scopeError.Text = _lang.Label("TEXT_SETUP_SCOPE_DENIED");
            _scopeError.IsVisible = true;
            return;
        }

        _scopeError.IsVisible = false;
        _plan = chosen;

        // 换作用域就把位置换成那一个作用域自己的默认目录（Program Files ↔ %LOCALAPPDATA%\Programs）。
        // 用户在位置页填的那一个跟着没掉，这是故意的：两个作用域的默认值根本不是一回事，
        // 留着 C:\Users\…\Programs\Andzip 去装机器级，等于把"给所有人"偷偷降级成"只给自己"。
        _location.Text = chosen.InstallDirectory;
        GoTo(WizardPage.Location);
    }

    /// <summary>
    /// 换作用域算一份新落点。<strong>测试给的落点不换目录</strong>：那几处临时目录与探针键是
    /// "整条往返真跑一遍而不碰开发机"的全部凭据，换成真的 Program Files 就等于往开发机上装东西。
    /// </summary>
    private InstallPlan PlanForScope(InstallScope scope)
        => _given is null ? _service.CurrentPlan(scope) : _given with { Scope = scope };

    private Control Location()
    {
        var body = Stack();
        body.Children.Add(Heading("TEXT_SETUP_LOCATION_TITLE"));
        body.Children.Add(Paragraph("TEXT_SETUP_LOCATION_HINT"));
        body.Children.Add(Text("TEXT_INSTALLER_TARGET", Design.Type.Caption, secondary: true));

        var path = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_location, 0);
        Grid.SetColumn(_browse, 1);
        _browse.Margin = new Thickness(Design.Space.X2, 0, 0, 0);
        path.Children.Add(_location);
        path.Children.Add(_browse);
        body.Children.Add(path);

        body.Children.Add(SpaceRow("TEXT_SETUP_SPACE_REQUIRED", _spaceRequired));
        body.Children.Add(SpaceRow("TEXT_SETUP_SPACE_AVAILABLE", _spaceAvailable));

        _locationError.Foreground = Design.Semantic.Danger;
        _locationError.FontSize = Design.Type.Caption;
        _locationError.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_locationError);

        body.Children.Add(Text("TEXT_SETUP_OPTIONS_TITLE", Design.Type.Caption, secondary: true));
        body.Children.Add(_associate);
        body.Children.Add(_contextMenu);

        return body;
    }

    private Control Working()
    {
        var body = Stack(Design.Space.X4);

        _workingHeading.Foreground = Design.Ink.Primary;
        _workingHeading.FontSize = Design.Type.Subtitle;
        _workingHeading.FontWeight = Design.Type.Semibold;
        _workingHeading.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_workingHeading);

        _workingStep.Foreground = Design.Ink.Primary;
        _workingStep.FontSize = Design.Type.Body;
        _workingStep.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(_workingStep);
        body.Children.Add(_bar);

        return body;
    }

    private Control Done()
    {
        var body = Stack();

        _doneHeading.Foreground = Design.Ink.Primary;
        _doneHeading.FontSize = Design.Type.Subtitle;
        _doneHeading.FontWeight = Design.Type.Semibold;
        _doneHeading.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_doneHeading);

        _doneText.Foreground = Design.Ink.Secondary;
        _doneText.FontSize = Design.Type.Body;
        _doneText.TextWrapping = TextWrapping.Wrap;
        _doneText.LineHeight = Design.Type.BodyLineHeight;
        body.Children.Add(_doneText);

        _uninstallHint.Foreground = Design.Ink.Secondary;
        _uninstallHint.FontSize = Design.Type.Caption;
        _uninstallHint.TextWrapping = TextWrapping.Wrap;
        _uninstallHint.Text = _lang.Label("TEXT_SETUP_UNINSTALL_HINT");
        body.Children.Add(_uninstallHint);
        body.Children.Add(_runAfter);

        return body;
    }

    private Control ConfirmUninstall()
    {
        var body = Stack();
        body.Children.Add(Heading("TEXT_SETUP_CONFIRM_TITLE"));

        _confirmText.Foreground = Design.Ink.Secondary;
        _confirmText.FontSize = Design.Type.Body;
        _confirmText.TextWrapping = TextWrapping.Wrap;
        _confirmText.LineHeight = Design.Type.BodyLineHeight;
        body.Children.Add(_confirmText);

        return body;
    }

    // -------------------------------------------------------------- 动作

    /// <summary>从安装向导翻到卸载向导（这台机器已经装过时，欢迎页上才有那颗按钮）。</summary>
    private void EnterUninstall()
    {
        _uninstaller = true;
        _plan = _given ?? _service.RecordedPlan();
        GoTo(WizardPage.ConfirmUninstall);
    }

    /// <summary>
    /// 位置那一页的「安装」：先确认这个路径能用、写得进去，再放行。
    /// <para>提前量而不是事后量的理由：<c>Program Files</c> 那一类要管理员的位置，
    /// 走到进度条上才失败会留下一个空目录和一个说不清的界面。</para>
    /// </summary>
    private void Install()
    {
        if (InstallTarget.Normalize(_location.Text) is not { } directory)
        {
            ShowLocationError(_lang.Label("TEXT_SETUP_DIR_INVALID"));
            return;
        }

        if (!InstallTarget.IsWritable(directory))
        {
            ShowLocationError(_lang.Format("TEXT_SETUP_DIR_UNWRITABLE", ("dir", _plan.InstallDirectory)));
            return;
        }

        _plan = PlanAt(directory);
        _locationError.IsVisible = false;
        Run(installing: true);
    }

    private void Uninstall() => Run(installing: false);

    /// <summary>
    /// 只换程序文件那两处，其余（开始菜单、桌面、卸载表键名）保持构造时算好的那份。
    /// <para>不能整份重算：<c>CurrentPlan()</c> 走的是这台机器的真实目录，
    /// 测试传进来的临时目录与探针键会被它冲掉，于是"点安装真的跑一遍"那条测试
    /// 就变成往开发机上装东西。</para>
    /// </summary>
    private InstallPlan PlanAt(string directory) => _plan with
    {
        InstallDirectory = directory,
        ExecutablePath = Path.Combine(directory, InstallLayout.ExeName),
    };

    /// <summary>跑一次安装或卸载。文件复制要一两秒，所以在别的线程上；界面只报每一步与结果。</summary>
    private async void Run(bool installing)
    {
        _busy = true;
        _bar.Value = 0;
        _workingStep.Text = string.Empty;
        GoTo(WizardPage.Working);

        // 开关的状态必须在<strong>进后台线程之前</strong>读出来：SettingToggle 背后是 Avalonia 的
        // ToggleButton，在别的线程上碰它的属性直接抛 "Call from invalid thread"
        //（1.8.1 装不上就是栽在这一行）。
        var associate = _associate.IsChecked == true;
        var contextMenu = _contextMenu.IsChecked == true;

        var progress = new UiProgress<ProgressStep>(step =>
        {
            _workingStep.Text = _lang.Label(step.MessageKey);
            _bar.Value = step.Percent;
        });

        try
        {
            if (installing)
            {
                var source = Environment.ProcessPath
                    ?? throw new InvalidOperationException("拿不到自己的 exe 路径，无法复制。");

                var outcome = await Task.Run(() => _service.Install(_plan, source, associate, contextMenu, progress));
                _result = DoneMessage(outcome);
            }
            else
            {
                var cleaned = await Task.Run(() => _service.Uninstall(_plan, progress));
                _result = cleaned
                    ? _lang.Label("TEXT_INSTALLER_REMOVED")
                    : _lang.Format("TEXT_INSTALLER_LEFT", ("dir", _plan.InstallDirectory));
            }
        }
        catch (Exception ex)
        {
            // 装一半失败必须说清楚，而且要说人话：异常原文是英文的，直接甩在界面上就是
            // 1.9.2 真机核对里那行 "Access to the path 'Andzip.exe' is denied."
            _result = _lang.Format("TEXT_SETUP_FAILED", ("reason", ex.Message));
        }
        finally
        {
            _busy = false;
            _bar.Value = 100;
        }

        GoTo(WizardPage.Done);
    }

    /// <summary>装完之后那句回话：桌面那一个到底建没建，只有服务知道，不能写死。</summary>
    private string DoneMessage(InstallOutcome outcome)
    {
        var message = _lang.Label(outcome.DesktopShortcut
            ? "TEXT_INSTALLER_DONE"
            : "TEXT_INSTALLER_DONE_NO_DESKTOP");

        return outcome.BlockedExtensions.Count > 0
            ? message + "\n" + _lang.Label("TEXT_INSTALLER_BLOCKED") + string.Join("  ", outcome.BlockedExtensions)
            : message;
    }

    /// <summary>完成页的「完成」：按勾选拉起刚装好的那一个，然后收窗。</summary>
    private void Finish()
    {
        if (!_uninstaller && _runAfter.IsChecked == true)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _plan.ExecutablePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // 只是"没顺手帮你开起来"，不该让已经装好的东西看起来像失败了
            }
        }

        Close();
    }

    // -------------------------------------------------------------- 空间与提示

    /// <summary>系统那个文件夹选择框。取不到（headless、无桌面环境）就当用户没改。</summary>
    private async Task BrowseAsync()
    {
        IStorageFolder? startAt = null;
        var current = InstallTarget.Normalize(_location.Text);
        if (current is not null && Directory.Exists(current))
        {
            startAt = await StorageProvider.TryGetFolderFromPathAsync(current);
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _lang.Label("TEXT_SETUP_LOCATION_TITLE"),
            AllowMultiple = false,
            SuggestedStartLocation = startAt,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            _location.Text = path;
        }
    }

    /// <summary>所需空间 = 复制过去那一个 exe 的实际字节；可用空间 = 目标所在卷还剩多少。</summary>
    private void RefreshSpace()
    {
        var source = Environment.ProcessPath;
        var unknown = _lang.Label("TEXT_SETUP_SPACE_UNKNOWN");

        _spaceRequired.Text = source is { Length: > 0 } && File.Exists(source)
            ? SizeText.Format(InstallTarget.RequiredBytes(source))
            : unknown;

        _spaceAvailable.Text = InstallTarget.AvailableBytes(_location.Text) is { } bytes
            ? SizeText.Format(bytes)
            : unknown;
    }

    private void ShowLocationError(string message)
    {
        _locationError.Text = message;
        _locationError.IsVisible = true;
    }

    private bool Installed() => File.Exists(_plan.ExecutablePath);

    // -------------------------------------------------------------- 排版小件

    private static StackPanel Stack(double spacing = Design.Space.X3) => new()
    {
        Margin = new Thickness(Design.Space.X6),
        Spacing = spacing,
    };

    private Control Heading(string key) => new TextBlock
    {
        Text = _lang.Label(key),
        Foreground = Design.Ink.Primary,
        FontSize = Design.Type.Subtitle,
        FontWeight = Design.Type.Semibold,
        TextWrapping = TextWrapping.Wrap,
    };

    private Control Text(string key, double size, bool secondary = false) => ParagraphText(
        _lang.Label(key), size, secondary ? Design.Ink.Secondary : Design.Ink.Primary);

    private Control Paragraph(string key) => ParagraphText(_lang.Label(key));

    private static TextBlock ParagraphText(string text, double size = Design.Type.Body, IBrush? ink = null) => new()
    {
        Text = text,
        Foreground = ink ?? Design.Ink.Secondary,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Design.Type.BodyLineHeight,
    };

    private Control SpaceRow(string labelKey, TextBlock value)
    {
        value.Foreground = Design.Ink.Primary;
        value.FontSize = Design.Type.Body;

        var label = new TextBlock
        {
            Text = _lang.Label(labelKey),
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, Design.Space.X3, 0),
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }
}
