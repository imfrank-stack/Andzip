using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Andzip.App.Views;

/// <summary>
/// 自绘标题栏（1.7.0）：窗口不再有系统那条方方的标题栏，整块圆角面板的最上面一行就是它。
/// <para>为什么值得自己做：① 系统标题栏是直角的，主框体再怎么圆，顶上一横条是方的就会露馅；
/// ② 深色模式下系统标题栏翻不深（这台 Win10 上 DWM 属性返回成功但屏幕不变，见开发记录第七节第 28 条），
/// 自己画的那一条跟着调色板走，问题自然消失。</para>
/// <para><strong>用的是"扩展客户端区"而不是 <c>SystemDecorations=None</c></strong>：
/// 前者保留系统给的贴边缩放、Win+方向键分屏、任务栏高度让位，后者这些全要自己实现。
/// 我们只把按钮和标题画回来。</para>
/// </summary>
internal sealed class TitleBar : Border
{
    private const double ButtonWidth = 46;
    private const double BarHeight = 40;

    private readonly Window _window;
    private readonly LangCatalog _lang;
    private readonly TextBlock _caption;
    private readonly Button? _minimize;
    private readonly Button? _maximize;
    private readonly Button _close;
    private readonly List<(Button Button, Border Chip, Path Glyph, bool Danger)> _buttons = new();

    /// <param name="resizable">给不给最小化/最大化两个按钮。对话框与进度窗口不给。</param>
    /// <param name="onClose">叉号要做的事，默认"关掉窗口"；进度窗口换成"取消这件事并关掉"。</param>
    /// <param name="leading">图标右边那一块。**主窗口给的是菜单栏** —— 参考软件那一行就是
    /// "图标 + 菜单 + 三个按钮"，而我们以前把标题文字单独占一行，两行才装完。
    /// 给了它就<strong>不再显示标题文字</strong>：那一行的位置让给菜单了。
    /// 窗口自己的 <c>Title</c> 照旧（任务栏、Alt+Tab、读屏器都读它，"标题栏显示完整路径"
    /// 那项设置也仍然生效），只是不在这条上再印一遍。</param>
    /// <param name="trailing">挤在按钮左边的那一小块（主窗口放版本号 —— 截图要能自报身份，
    /// 这是 1.9.1 定的规矩；把它从标题里挪到这里，而不是删掉）。</param>
    public TitleBar(
        Window window,
        LangCatalog lang,
        bool resizable,
        Action? onClose = null,
        Control? leading = null,
        Control? trailing = null)
    {
        _window = window;
        _lang = lang;
        Height = BarHeight;
        Background = Brushes.Transparent;
        Padding = new Thickness(Design.Space.X2, 0, 0, 0);

        var mark = new Border
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Design.Space.X1, 0, Design.Space.X2, 0),
        };

        // 与任务栏、桌面快捷方式是同一张图：标题栏里那枚换成另一套画法，
        // 用户就会看到"桌面上一个图标、窗口里另一个图标"。用户自己调过配色之后，
        // 这句话反过来也成立 —— 所以这一枚跟着 Assets 换，不是构造时抄一次。
        void PaintMark() => mark.Child = Assets.AppArt is { } art
            ? new Image { Source = art, Width = 16, Height = 16 }
            : Icons.AppMark(16);

        PaintMark();
        Assets.IconChanged += PaintMark;
        DetachedFromVisualTree += (_, _) => Assets.IconChanged -= PaintMark;

        _caption = new TextBlock
        {
            FontSize = Design.Type.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, Design.Space.X3, 0),
        };
        // 绑到 Window.Title 而不是抄一份：主窗口在开包/关包/另存为时反复改 Title，
        // 抄一份就会看到"标题栏还写着上一个包"。
        _caption.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(mark);
        if (leading is not null)
        {
            leading.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(leading);
        }
        else
        {
            left.Children.Add(_caption);
        }

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            trailing.Margin = new Thickness(0, 0, Design.Space.X2, 0);
            right.Children.Add(trailing);
        }

        if (resizable)
        {
            var minimize = WindowSlot(Icons.Minimize, "TEXT_TITLEBAR_MINIMIZE", () => _window.WindowState = WindowState.Minimized);
            var maximize = WindowSlot(Icons.Maximize, "TEXT_TITLEBAR_MAXIMIZE", ToggleMaximize);
            _minimize = minimize.Button;
            _maximize = maximize.Button;
            right.Children.Add(minimize.Host);
            right.Children.Add(maximize.Host);
        }

        var close = WindowSlot(Icons.Close, "TEXT_TITLEBAR_CLOSE",
            onClose ?? (() => window.Close()), danger: true);
        _close = close.Button;
        right.Children.Add(close.Host);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Child = grid;

        // 拖动与双击最大化：点在按钮上时不抢（按钮自己会处理，这里只在空白处生效）。
        PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && IsInsideButton(c))
            {
                return;
            }

            if (e.ClickCount == 2 && _window.CanResize)
            {
                ToggleMaximize();
                return;
            }

            if (e.Properties.IsLeftButtonPressed)
            {
                _window.BeginMoveDrag(e);
            }
        };

        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                PaintMaximizeGlyph();
            }
        };

        Repaint();
    }

    /// <summary>
    /// 换主题后重取一遍令牌。标题栏上的每个颜色都是构造时从 <see cref="Design"/> 抄的，
    /// 不重画就会留下"深色窗口配一条浅色标题栏"。
    /// </summary>
    public void Repaint()
    {
        _caption.Foreground = Design.Ink.Primary;
        foreach (var (button, chip, glyph, danger) in _buttons)
        {
            Paint(button, chip, glyph, danger);
        }
    }

    /// <summary>
    /// 一颗 46×40 的命中区，里面是一枚会浮起的圆角药丸。
    /// <para>关闭按钮原来悬停时整块刷成实心红（Windows 的习惯），用户的评价是<strong>突兀</strong>：
    /// 一块 46×40 的方形红块压在圆角面板的角上，比它警告的那件事还抢眼。
    /// 现在改成淡红底 + 红色叉，"危险"这件事由颜色本身说，不由面积说。</para>
    /// </summary>
    /// <summary>
    /// 一颗按钮 = 一层"药丸 + 按钮"的宿主。宿主进 StackPanel，按钮留给调用方握着
    /// （最大化那一项事后要换图标与无障碍名）。
    /// </summary>
    private (Control Host, Button Button) WindowSlot(string geometry, string nameKey, Action onClick, bool danger = false)
    {
        var glyph = Icons.WindowGlyph(geometry, Design.IconSize.Inline, Design.Ink.Secondary);
        var button = new Button
        {
            Content = glyph,
            Width = ButtonWidth,
            Height = BarHeight,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = Design.Radius.None,
        }.PinFace();   // 小圆片在底下，面皮必须一直是透明的（主题的 :pressed 会涂一块深灰，1.8.8）

        var chip = new Border
        {
            Margin = new Thickness(5),
            CornerRadius = Design.Radius.Md,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        var host = new Panel { Children = { chip, button } };

        Avalonia.Automation.AutomationProperties.SetName(button, _lang.Label(nameKey));
        button.Click += (_, _) => onClick();
        button.PointerEntered += (_, _) => Paint(button, chip, glyph, danger);
        button.PointerExited += (_, _) => Paint(button, chip, glyph, danger);

        Paint(button, chip, glyph, danger);
        _buttons.Add((button, chip, glyph, danger));
        return (host, button);
    }

    /// <summary>按"指针在不在上面"画这一颗按钮。换主题与指针进出走的都是这一个方法。</summary>
    private void Paint(Button button, Border chip, Path glyph, bool danger)
    {
        var hot = button.IsPointerOver && button.IsEnabled;

        chip.Background = hot
            ? danger ? Design.Semantic.DangerTint : Design.Surface.Muted
            : Brushes.Transparent;
        chip.BoxShadow = hot ? Design.Shadow.Lift : default;
        glyph.Stroke = hot
            ? danger ? Design.Semantic.Danger : Design.Ink.Primary
            : Design.Ink.Secondary;
    }

    private void ToggleMaximize()
        => _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void PaintMaximizeGlyph()
    {
        if (_maximize?.Content is not Path glyph)
        {
            return;
        }

        var maximized = _window.WindowState == WindowState.Maximized;
        glyph.Data = Geometry.Parse(maximized ? Icons.Restore : Icons.Maximize);
        // 无障碍名要跟着换：读屏里一直念"最大化"，而按钮此时干的是还原。
        Avalonia.Automation.AutomationProperties.SetName(
            _maximize, _lang.Label(maximized ? "TEXT_TITLEBAR_RESTORE" : "TEXT_TITLEBAR_MAXIMIZE"));
    }

    /// <summary>
    /// 这一下点在了"自己会处理点击"的东西上，别抢去当拖动。
    /// <para><c>MenuItem</c> 必须算进来：2.4.0 把菜单栏搬进这一行之后，
    /// 只认 <c>Button</c> 的话，点「文件(F)」会同时做两件事 —— 展开菜单<strong>和</strong>拖动窗口。</para>
    /// </summary>
    private bool IsInsideButton(Control source)
    {
        for (Control? at = source; at is not null && at != this; at = at.Parent as Control)
        {
            if (at is Button or MenuItem)
            {
                return true;
            }
        }

        return false;
    }

}
