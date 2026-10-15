using System.Diagnostics;
using Andzip.Core;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「帮助 → 关于 Andzip」（F1）。
/// <para>存在的理由是把四件"用户迟早要问、而界面上别处读不到"的事一次说清：
/// 这是<strong>哪一版</strong>、压缩内核<strong>到底加载没有</strong>（没加载时标题栏那句
/// "compression engine unavailable" 会在这里重复一遍，不粉饰）、
/// 许可证是什么（这个 exe 里合并了 7-Zip 与几个编码器，这一句不是装饰），
/// 以及出了问题去哪儿说。</para>
/// <para>目录那一行显示的是<strong>这个正在运行的 exe 所在的目录</strong>而不是"安装目录"四个字 ——
/// 绿色版与安装版、甚至同一台机器上装了两份，都答得准；写死一个"Program Files"就会有一半天是错的。</para>
/// </summary>
public sealed class AboutWindow : Window
{
    public AboutWindow(LangCatalog lang)
    {
        Title = lang.Label("TEXT_TITLE_ABOUT");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X3,
            Margin = new Thickness(0, 0, 0, Design.Space.X4),
        };
        if (Assets.AppArt is { } art)
        {
            heading.Children.Add(new Image
            {
                Source = art,
                Width = 64,
                Height = 64,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        heading.Children.Add(new StackPanel
        {
            Spacing = Design.Space.X1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "Andzip (Professional)",
                    FontSize = Design.Type.Title,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Design.Ink.Primary,
                },
                new TextBlock
                {
                    Text = lang.Label("TEXT_ABOUT_TAGLINE"),
                    FontSize = Design.Type.Caption,
                    Foreground = Design.Ink.Secondary,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });

        var rows = new StackPanel { Spacing = Design.Space.X2 };
        foreach (var (label, value) in Facts(lang))
        {
            rows.Children.Add(Row(lang, label, value));
        }

        var homepage = new Button
        {
            Content = lang.Format("TEXT_ABOUT_HOMEPAGE", ("url", AndzipLinks.Homepage)),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 0,
        }.Ghost();
        homepage.Click += (_, _) => OpenUrl(AndzipLinks.Homepage);

        var close = new Button
        {
            Content = lang.Label("TEXT_BTN_CLOSE"),
            MinWidth = 96,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        }.Ghost();
        close.Click += (_, _) => Close();

        var body = new StackPanel
        {
            Margin = new Thickness(Design.Space.X5, Design.Space.X4),
            Spacing = Design.Space.X3,
            Children = { heading, rows, homepage, close },
        };

        this.Chromeless(lang, body);
    }

    /// <summary>
    /// 「关于」那一屏的读数。<para>internal 而不是藏在构造函数里：这几行的内容是真话还是占位，
    /// 只有把它当数据读出来才断言得了（界面上找到一段文字，证明不了它不是抄来的旧值）。</para>
    /// </summary>
    internal static IReadOnlyList<(string LabelKey, string Value)> Facts(LangCatalog lang)
    {
        var version = AppIdentity.Version;

        var directory = AppDirectory();

        return
        [
            ("TEXT_ABOUT_VERSION", version),
            ("TEXT_ABOUT_ENGINE", ArchiveEngine.Describe()),
            ("TEXT_ABOUT_LICENSE", lang.Label("TEXT_ABOUT_LICENSE_VALUE")),
            ("TEXT_ABOUT_FOLDER", directory is { } dir ? dir : lang.Label("TEXT_ABOUT_FOLDER_UNKNOWN")),
        ];
    }

    /// <summary>
    /// 这个正在运行的程序自己所在的目录。
    /// <para><see cref="Environment.ProcessPath"/> 在单文件发布下给的是外面那个 exe（不是解包临时目录），
    /// 所以绿色版、安装版、以及一台机器上装了两份这三种情形都答得准。</para>
    /// </summary>
    internal static string? AppDirectory()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(exe));
        return Directory.Exists(directory) ? directory : null;
    }

    private static Control Row(LangCatalog lang, string labelKey, string value)
    {
        var label = new TextBlock
        {
            Text = lang.Label(labelKey),
            FontSize = Design.Type.Caption,
            Foreground = Design.Ink.Secondary,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, Design.Space.X2, 0),
        };
        Grid.SetColumn(label, 0);

        var cell = new TextBlock
        {
            Text = value,
            FontSize = Design.Type.Body,
            Foreground = Design.Ink.Primary,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(cell, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { label, cell },
        };
    }

    /// <summary>
    /// 交给系统默认浏览器。<strong>打不开就什么都不做</strong>：关于窗口已经把所有信息都写在屏幕上了，
    /// 浏览器没起来（没有默认浏览器、精简系统）不该反过来告诉用户"你看的这一屏有问题"。
    /// </summary>
    internal static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
}
