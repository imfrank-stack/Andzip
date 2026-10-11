using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 首页（FR-1）：与压缩包视图同构的空白双栏 —— 左边树栏、右边内容栏，中间一条分隔线。
/// <para>
/// 之所以保持同样的骨架而不是另画一块欢迎页：打开压缩包后窗口尺寸与分栏位置都不跳，
/// 且「未打开」与「已打开」是同一个视图的两个状态，而不是两个界面。
/// </para>
/// <para>底部栏的两个视图切换按钮此时没有作用对象，保持禁用。</para>
/// </summary>
internal static class HomeView
{
    public static Control Create(LangCatalog lang)
    {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,4,*") };

        var left = new Border { Background = Brushes.White };
        var splitter = new GridSplitter
        {
            Width = 4,
            Background = new SolidColorBrush(Color.Parse("#D6D6D6")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var right = new Border { Background = Brushes.White };

        Grid.SetColumn(left, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(splitter);
        body.Children.Add(right);

        var bar = ViewBar.Create(
            ViewBar.Toggle(ViewIcon.List, lang.Get("TEXT_VIEW_DETAILS"), null),
            ViewBar.Toggle(ViewIcon.Grid, lang.Get("TEXT_VIEW_ICONS"), null));

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(body);
        return panel;
    }
}
