using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Andzip.App.Views;

/// <summary>
/// 视图底部栏（首页与压缩包视图共用）：浅灰一条，右侧依次是视图切换按钮与状态文字。
/// </summary>
internal static class ViewBar
{
    public static Border Create(params Control[] rightItems)
    {
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 3,
        };

        foreach (var item in rightItems)
        {
            right.Children.Add(item);
        }

        return new Border
        {
            Height = 30,
            Background = new SolidColorBrush(Color.Parse("#F0F0F0")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D0D0")),
            Child = right,
        };
    }

    /// <summary>图标按钮；<paramref name="onClick"/> 为 null 时是禁用态（首页还没有压缩包可看）。</summary>
    public static Button Toggle(string geometry, string tip, Action? onClick)
    {
        var button = new Button
        {
            Content = new ShapePath
            {
                Data = Geometry.Parse(geometry),
                Stroke = new SolidColorBrush(Color.Parse("#3C3C3C")),
                StrokeThickness = 1.3,
                Stretch = Stretch.Uniform,
                Width = 15,
                Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 26,
            Height = 23,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 0, 3),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsEnabled = onClick is not null,
        };

        ToolTip.SetTip(button, tip);

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    /// <summary>当前生效的视图按钮画成按下态。</summary>
    public static void Highlight(Button button, bool active)
    {
        button.Background = active ? new SolidColorBrush(Color.Parse("#CBE6FA")) : Brushes.Transparent;
        button.BorderThickness = new Thickness(active ? 1 : 0);
        button.BorderBrush = new SolidColorBrush(Color.Parse("#8FBEE8"));
    }
}

/// <summary>底部栏三个视图按钮的线稿图标。</summary>
internal static class ViewIcon
{
    public const string List = "M0,1.5 h2 v2 h-2 z M4,2 h11 M0,6 h2 v2 h-2 z M4,6.5 h11 M0,10.5 h2 v2 h-2 z M4,11 h11";
    public const string Grid = "M0,0 h6 v6 h-6 z M9,0 h6 v6 h-6 z M0,9 h6 v6 h-6 z M9,9 h6 v6 h-6 z";
    public const string Folder = "M0,3 h5 l1.4,2 H14 V13 H0 z";
}
