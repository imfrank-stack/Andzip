using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 视图底部栏（首页与压缩包视图共用）：右侧依次是视图切换按钮与状态文字。
/// <para>1.7.0 起它与上面的内容在同一张表面上，只靠顶上一根发丝线分开 ——
/// 上一版它是第四张卡片，一屏四块白板就是用户说的"割裂感"。</para>
/// <para>按钮的悬停色：静止透明、悬停压深一档、选中淡蓝。白底上"提亮"等于没有反馈，
/// 所以方向与工具栏一致（规范 3.1 末）。</para>
/// <para>按钮命中区按规范第八节抬到 30×26 以上。</para>
/// </summary>
internal static class ViewBar
{
    public static Control Create(params Control[] rightItems)
    {
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = Design.Space.X1,
            Margin = new Thickness(0, 0, Design.Space.X3, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var item in rightItems)
        {
            right.Children.Add(item);
        }

        var row = new Border
        {
            Height = 34,
            Background = Brushes.Transparent,
            Child = right,
        };

        // 发丝线单独一层：Border 的上边线会把 34px 的内容挤掉 1px，Rectangle 不会。
        var dock = new DockPanel();
        var line = Seam.Line();
        DockPanel.SetDock(line, Dock.Top);
        dock.Children.Add(line);
        dock.Children.Add(row);
        return dock;
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
        var hot = hover && button.IsEnabled;

        // 静止透明、悬停压深一档、选中淡蓝 —— 三者必须在白卡片上彼此分得开。
        button.Background = active ? Design.Brand.Soft : hot ? Design.Line.Subtle : Brushes.Transparent;
        button.BorderBrush = active ? Design.Brand.Line : hot ? Design.Line.Strong : Brushes.Transparent;
        button.BorderThickness = new Thickness(active || hot ? 1 : 0);
    }
}
