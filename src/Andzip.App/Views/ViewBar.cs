using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 视图底部栏（首页与压缩包视图共用）：浅灰一条，右侧依次是视图切换按钮与状态文字。
/// <para>层级靠底色差（内容白、这条浅灰）+ 1px 上边线表达 —— SimpleTheme 用不了阴影，
/// 见规范第七节。按钮命中区按规范第八节抬到 30×26 以上。</para>
/// </summary>
internal static class ViewBar
{
    public static Border Create(params Control[] rightItems)
    {
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = Design.Space.X1,
            Margin = new Thickness(0, 0, Design.Space.X2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var item in rightItems)
        {
            right.Children.Add(item);
        }

        return new Border
        {
            Height = 34,
            Background = Design.Surface.Muted,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Design.Line.Subtle,
            Child = right,
        };
    }

    /// <summary>图标按钮；<paramref name="onClick"/> 为 null 时是禁用态（首页还没有压缩包可看）。</summary>
    public static Button Toggle(string geometry, string tip, Action? onClick)
    {
        var button = new Button
        {
            Content = Icons.Line(geometry, Design.IconSize.Inline, Design.Icon.Glyph, 1.3),
            Width = 30,
            Height = 26,
            Padding = new Thickness(Design.Space.X1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = Design.Radius.Sm,
            IsEnabled = onClick is not null,
        };

        ToolTip.SetTip(button, tip);

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        // 这三个按钮的"当前生效"是由 Highlight 事后改底色的，所以悬停态不能像 Chrome 那样
        // 记住调用时的静止色 —— 那样一悬停就把选中高亮冲掉了。状态一律从 Tag 现算。
        button.PointerEntered += (_, _) => Paint(button, hover: true);
        button.PointerExited += (_, _) => Paint(button, hover: false);
        Paint(button, hover: false);
        return button;
    }

    /// <summary>当前生效的视图按钮画成按下态。</summary>
    public static void Highlight(Button button, bool active)
    {
        button.Tag = active;
        Paint(button, hover: button.IsPointerOver);
    }

    private static void Paint(Button button, bool hover)
    {
        var active = Equals(button.Tag, true);

        button.Background = active ? Design.Brand.Soft : hover && button.IsEnabled ? Design.Surface.Base : Brushes.Transparent;
        button.BorderBrush = active ? Design.Brand.Line : hover && button.IsEnabled ? Design.Line.Subtle : Brushes.Transparent;
        button.BorderThickness = new Thickness(active || hover && button.IsEnabled ? 1 : 0);
    }
}
