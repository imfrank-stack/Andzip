using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 首页（FR-1）：与压缩包视图同构的空白双栏 —— 左边树栏、右边内容栏，中间一根可拖的发丝线。
/// <para>
/// 之所以保持同样的骨架而不是另画一块欢迎页：打开压缩包后窗口尺寸与分栏位置都不跳，
/// 且「未打开」与「已打开」是同一个视图的两个状态，而不是两个界面。
/// </para>
/// <para>
/// 1.7.0 起两块不再各自是一张卡片：<strong>整块窗口就是一张面板</strong>，
/// 顶部的标题栏 / 菜单 / 工具栏与下面的内容之间没有"卡片缝"，只有一根分隔树与列表的细线。
/// 上一版那种"外壳卡 + 左卡 + 右卡 + 底栏卡"四块浮板，用户的评价是<em>割裂</em> ——
/// 数据工具的一屏不该被切成四块各自发光的白板。
/// </para>
/// <para>底部栏的两个视图切换按钮此时没有作用对象，保持禁用。</para>
/// </summary>
internal static class HomeView
{
    public static Control Create(LangCatalog lang)
    {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,8,*") };

        var left = new Border();
        var right = new Border();

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(right);
        body.Children.Add(Seam.Splitter(1));

        var bar = ViewBar.Create(
            ViewBar.Toggle(Icons.ListMode, lang.Label("TEXT_VIEW_DETAILS"), null),
            ViewBar.Toggle(Icons.IconMode, lang.Label("TEXT_VIEW_ICONS"), null));

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(body);
        return panel;
    }
}
