using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
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
    /// <para><strong>线 1px、命中 8px，但只能是一枚控件</strong>：以前为了这两层，
    /// 把 <c>GridSplitter</c> 套进一层宿主 <c>Grid</c> —— 而它只改<strong>直接父级</strong>那枚 Grid
    /// 的列，于是它伸手去改的是那层 8px 宽的宿主，外面那三列一动不动：光标对、按得住、
    /// 拖起来很顺，布局没变（1.8.9 用户反馈"这条线应该可以拖，现在无法调整"）。
    /// 现在两层收进它自己的模板：命中层就是它本身，那根线是模板里的一枚 <c>Rectangle</c>。</para>
    /// <para>模板根必须是<strong>可命中的填充</strong>（<c>Transparent</c> 算填充，null 不算），
    /// 否则整个 8px 带子都点不着，只剩中间那 1px。</para>
    /// <para>上一版这里是一根 4px 的 <c>Border.Strong</c> 粗杠，那是"没有卡片可分块"时的做法，
    /// 现在它会把一整块表面劈成两半，正是用户说的那种割裂感。1.8.0 再退一步：颜色从
    /// <c>Line.Subtle</c> 换到最淡的 <c>Line.Hair</c>，并且<strong>上下各留 8px 不贴边</strong> ——
    /// 量出来它其实只有 1px（1.5 倍缩放下 2 个物理像素），粗的不是线，是它<strong>顶天立地</strong>：
    /// 一根贯通的线把面板切成两个"框"，而这两框本来是一整张表面。</para>
    /// </summary>
    public static Control Splitter(int column)
    {
        var seam = new GridSplitter
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            // 一根会动的线要长得像能拖：指针压上去就是左右箭头，不用等用户猜。
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Template = new FuncControlTemplate<GridSplitter>((_, _) => new Panel
            {
                Background = Brushes.Transparent,
                Children =
                {
                    new Rectangle
                    {
                        Width = 1,
                        Margin = new Thickness(0, Design.Space.X2),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Fill = Design.Line.Hair,
                        Stretch = Stretch.Fill,
                    },
                },
            }),
        };

        Grid.SetColumn(seam, column);
        return seam;
    }

    /// <summary>一条横线：内容区与底部栏之间。用最淡的一档，见 <see cref="Splitter"/> 那段。</summary>
    public static Control Line(double thickness = 1) => new Rectangle
    {
        Height = thickness,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        Fill = Design.Line.Hair,
        Stretch = Stretch.Fill,
    };
}
