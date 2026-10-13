using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 分区用的"发丝线"。1.7.0 起整块窗口是一张面板，内部不再靠卡片之间的间隙分层，
/// 只靠这几根 1px 的线 —— 所以它们得统一从这里出，别处不许现场写 <c>BorderThickness</c>。
/// </summary>
internal static class Seam
{
    /// <summary>
    /// 树与列表之间那根可以拖的竖线。
    /// <para>线只有 1px，但 <see cref="GridSplitter"/> 要 8px 才拖得住 ——
    /// 所以是"看得见的细线 + 盖在它上面的透明命中区"两层。
    /// 上一版这里是一根 4px 的 <c>Border.Strong</c> 粗杠，那是"没有卡片可分块"时的做法，
    /// 现在它会把一整块表面劈成两半，正是用户说的那种割裂感。</para>
    /// </summary>
    public static Control Splitter(int column)
    {
        var host = new Grid();

        host.Children.Add(new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = Design.Line.Subtle,
            Stretch = Stretch.Fill,
        });
        host.Children.Add(new GridSplitter
        {
            Width = 8,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        });

        Grid.SetColumn(host, column);
        return host;
    }

    /// <summary>一条横线：内容区与底部栏之间。</summary>
    public static Control Line(double thickness = 1) => new Rectangle
    {
        Height = thickness,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        Fill = Design.Line.Subtle,
        Stretch = Stretch.Fill,
    };
}
