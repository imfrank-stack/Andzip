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
    private readonly List<(Button Button, Path Glyph, bool Danger)> _buttons = new();

    /// <param name="resizable">给不给最小化/最大化两个按钮。对话框与进度窗口不给。</param>
    public TitleBar(Window window, LangCatalog lang, bool resizable)
    {
        _window = window;
        _lang = lang;
        Height = BarHeight;
        Background = Brushes.Transparent;
        Padding = new Thickness(Design.Space.X2, 0, 0, 0);

        var icon = new Border
        {
            Width = 16,
            Height = 16,
            Child = Icons.AppMark(16),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Design.Space.X1, 0, Design.Space.X2, 0),
        };

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
        left.Children.Add(icon);
        left.Children.Add(_caption);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (resizable)
        {
            _minimize = WindowButton(Icons.Minimize, "TEXT_TITLEBAR_MINIMIZE", () => _window.WindowState = WindowState.Minimized);
            _maximize = WindowButton(Icons.Maximize, "TEXT_TITLEBAR_MAXIMIZE", ToggleMaximize);
            right.Children.Add(_minimize);
            right.Children.Add(_maximize);
        }

        _close = WindowButton(Icons.Close, "TEXT_TITLEBAR_CLOSE", () => _window.Close(), danger: true);
        right.Children.Add(_close);

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
        foreach (var (button, glyph, danger) in _buttons)
        {
            glyph.Stroke = danger && button.IsPointerOver ? Design.Ink.OnBrand : Design.Ink.Secondary;
            button.Background = Brushes.Transparent;
        }
    }

    private Button WindowButton(string geometry, string nameKey, Action onClick, bool danger = false)
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
            CornerRadius = danger ? new CornerRadius(0, 0, 15, 0) : Design.Radius.None,
        };

        Avalonia.Automation.AutomationProperties.SetName(button, _lang.Label(nameKey));
        button.Click += (_, _) => onClick();

        button.PointerEntered += (_, _) =>
        {
            button.Background = danger ? Design.Semantic.Danger : Design.Surface.Muted;
            glyph.Stroke = danger ? Design.Ink.OnBrand : Design.Ink.Primary;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            glyph.Stroke = Design.Ink.Secondary;
        };

        _buttons.Add((button, glyph, danger));
        return button;
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

    private bool IsInsideButton(Control source)
    {
        for (Control? at = source; at is not null && at != this; at = at.Parent as Control)
        {
            if (at is Button)
            {
                return true;
            }
        }

        return false;
    }

}
