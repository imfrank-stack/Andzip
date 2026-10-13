using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.Core.Installation;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 安装器窗口：把绿色版那一个 exe 装成"正常安装"的样子。
/// <para><strong>为什么同一个二进制能既是程序又是安装器</strong>：判据是它自己的文件名 ——
/// 叫 <c>Andzip-Setup-*.exe</c> 时开这个窗口，叫 <c>Andzip.exe</c> 时开主窗口。
/// 好处是安装器与应用<strong>不可能版本不一致</strong>（出包只是复制改名一份文件），
/// 而且装进电脑的那个 exe 与绿色版是同一个东西，卸载表里那条也不会指向一个特制的壳。</para>
/// <para><strong>这里没有"选择安装目录"</strong>：整条产品线（关联、右键动词、设置文件）
/// 都按当前用户设计，装到 <c>Program Files</c> 反而要管理员权限，而 Andzip 一处都不需要写机器级配置。
/// 少一个可以自由填的路径输入框，就少一类"路径里有奇怪字符"的故障。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly InstallationService _service;
    private readonly InstallPlan _plan;
    private readonly TextBlock _status;
    private readonly SettingToggle _associate;
    private readonly SettingToggle _contextMenu;
    private readonly Button _install;
    private readonly Button _uninstall;

    public InstallerWindow(LangCatalog lang)
    {
        _lang = lang;
        _service = new InstallationService(lang);
        _plan = _service.CurrentPlan();

        Title = lang.Label("TEXT_TITLE_INSTALLER");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var installed = File.Exists(_plan.ExecutablePath);

        var body = new StackPanel
        {
            Margin = new Thickness(Design.Space.X6),
            Spacing = Design.Space.X3,
        };

        body.Children.Add(new TextBlock
        {
            Text = lang.Label("TEXT_INSTALLER_TARGET"),
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Caption,
        });

        body.Children.Add(new TextBlock
        {
            Text = _plan.InstallDirectory,
            Foreground = Design.Ink.Primary,
            FontSize = Design.Type.Body,
            FontWeight = Design.Type.Semibold,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = installed
                ? lang.Format("TEXT_INSTALLER_ALREADY", ("dir", _plan.ExecutablePath))
                : lang.Label("TEXT_INSTALLER_NOT_YET"),
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Body,
            TextWrapping = TextWrapping.Wrap,
        });

        _associate = SettingRow.Toggle(lang, "TEXT_INSTALLER_ASSOCIATE", "TEXT_INSTALLER_ASSOCIATE_HINT", () => { });
        _contextMenu = SettingRow.Toggle(lang, "TEXT_INSTALLER_CONTEXT", "TEXT_INSTALLER_CONTEXT_HINT", () => { });

        // 已经装过一次就不再把勾选框默认打开：那时用户点「安装」是要重装/补关联，
        // 悄悄改一遍他的默认程序设置是意外后果。
        _associate.IsChecked = !installed;
        _contextMenu.IsChecked = !installed;
        body.Children.Add(_associate);
        body.Children.Add(_contextMenu);

        _status = new TextBlock
        {
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Caption,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Design.Type.Caption + 6,
            IsVisible = false,
        };
        body.Children.Add(_status);

        _install = new Button { Content = lang.Label("TEXT_BTN_INSTALL"), MinWidth = 96, IsDefault = true }.Accent();
        _install.Click += async (_, _) => await RunAsync(installing: true);

        _uninstall = new Button { Content = lang.Label("TEXT_BTN_UNINSTALL"), MinWidth = 96 }.Danger();

        // 没装过就不给这颗按钮 —— 一个"点了没东西可删"的红色按钮，比少一个按钮更让人犹豫。
        // 装完之后它会自己出现（RunAsync 末尾按实际状态改可见性）。
        _uninstall.IsVisible = installed;
        _uninstall.Click += async (_, _) => await RunAsync(installing: false);

        var close = new Button { Content = lang.Label("TEXT_BTN_CLOSE"), MinWidth = 96, IsCancel = true }.Ghost();
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X3,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(_install);
        buttons.Children.Add(_uninstall);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        this.Chromeless(lang, body);
    }

    /// <summary>
    /// 跑一次安装或卸载。文件复制要一两秒，所以放在别的线程上，界面只负责把结果如实写出来。
    /// </summary>
    private async Task RunAsync(bool installing)
    {
        _install.IsEnabled = false;
        _uninstall.IsEnabled = false;
        Show(installing ? _lang.Label("TEXT_INSTALLER_WORKING") : _lang.Label("TEXT_INSTALLER_REMOVING"));

        try
        {
            if (installing)
            {
                var source = Environment.ProcessPath
                    ?? throw new InvalidOperationException("拿不到自己的 exe 路径，无法复制。");

                var outcome = await Task.Run(() => _service.Install(
                    _plan, source, _associate.IsChecked == true, _contextMenu.IsChecked == true));

                var message = _lang.Label("TEXT_INSTALLER_DONE");
                Show(outcome.BlockedExtensions.Count > 0
                    ? message + "\n" + _lang.Label("TEXT_INSTALLER_BLOCKED") + string.Join("  ", outcome.BlockedExtensions)
                    : message);
            }
            else
            {
                var cleaned = await Task.Run(() => _service.Uninstall(_plan));
                Show(cleaned
                    ? _lang.Label("TEXT_INSTALLER_REMOVED")
                    : _lang.Format("TEXT_INSTALLER_LEFT", ("dir", _plan.InstallDirectory)));
            }
        }
        catch (Exception ex)
        {
            // 装一半失败必须说清楚，不能留一个"看起来成功了"的界面
            Show(ex.Message);
        }

        // 按钮的可见与可用一律按<strong>磁盘上的实际状态</strong>重算，不按"刚才那次成功没成功"：
        // 复制失败一半时，界面上不该出现一个能点但点了没用的按钮。
        var installed = File.Exists(_plan.ExecutablePath);
        _install.IsEnabled = true;
        _uninstall.IsVisible = installed;
        _uninstall.IsEnabled = installed;
    }

    private void Show(string text)
    {
        _status.Text = text;
        _status.IsVisible = true;
    }
}
