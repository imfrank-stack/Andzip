using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Andzip.App;

/// <summary>
/// 全部线稿图标的几何定义与工厂。规范见 <c>docs/界面设计规范.md</c> 第六节 ——
/// 那里只写"每个图标要表达什么、视觉上必须出现什么"，路径以这里为准，
/// 免得文档与代码变成两份会互相撒谎的真相。
/// <para>统一约定：24×24 视框、四周留 2 安全边距、描边 1.5、
/// <see cref="PenLineCap.Round"/> 端点与 <see cref="PenLineJoin.Round"/> 连接
/// —— 圆头是"平滑"在图标上的落点，比加阴影便宜得多。</para>
/// </summary>
internal static class Icons
{
    /// <summary>描边宽度。40px 显示时靠等比缩放自然变粗，不额外加粗。</summary>
    public const double Stroke = 1.5;

    // ---------- 工具栏 ----------
    //
    // 这一排原来是线稿（打开 / 解压 / 新建 / 添加 / 删除 / 测试 / 扫描），2.6.0 起换成
    // assets/svg/*.svg 那九枚双色图形，由 SvgGlyph 直接读设计稿画 —— 七个常量就此没了用途。
    // 留着不会出错，但会留下"代码里有一份、设计稿里有一份"的第二个真相，而两份会开始各说各话。

    /// <summary>查看：三行列表，左侧短标 + 右侧长线。</summary>
    public const string View = "M4.5,6 H6 M9,6 H20 M4.5,12 H6 M9,12 H20 M4.5,18 H6 M9,18 H20";

    // ---------- 内容区 ----------

    /// <summary>文件夹（唯一允许填充的图标：它在内容区出现频率最高）。</summary>
    public const string FolderGeometry =
        "M2,7.5 Q2,6 3.5,6 H8 L10,8.5 H20.5 Q22,8.5 22,10 V18.5 Q22,20 20.5,20 H3.5 Q2,20 2,18.5 Z";

    /// <summary>文件：矩形 + 右上折角。</summary>
    public const string FileGeometry =
        "M5,3.5 Q5,2 6.5,2 H12 L18,8 V20.5 Q18,22 16.5,22 H6.5 Q5,22 5,20.5 Z M12,2 V6.5 Q12,8 13.5,8 H18";

    // ---------- 视图栏 ----------

    /// <summary>详情列表模式。</summary>
    public const string ListMode = View;

    /// <summary>图标视图模式：2×2 方块。</summary>
    public const string IconMode =
        "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z";

    /// <summary>打开位置：方框 + 右上角外指箭头。</summary>
    public const string Locate =
        "M13,4 H5 Q3,4 3,6 V19 Q3,21 5,21 H18 Q20,21 20,19 V11 M14,4 H20 V10 M20,4 L11.5,12.5";

    // ---------- 提示 ----------

    /// <summary>信息：圆 + 小写 i。点用零长度段 + 圆头端点画出来。</summary>
    public const string Info = "M12,21 A9,9 0 1 1 12,3 A9,9 0 1 1 12,21 Z M12,11 V16.5 M12,7.5 V7.51";

    /// <summary>警告：圆角三角 + 感叹号。</summary>
    public const string Warning = "M12,3.5 L21,19.5 H3 Z M12,9.5 V14.5 M12,17 V17.01";

    // ---------- 窗口按钮（1.7.0 自绘标题栏） ----------
    //
    // 这三个不是"功能图标"而是系统符号，所以画得比第六节那套更轻：描边 1.1、
    // 形状保持横平竖直。系统按钮的可辨识度优先于风格统一 ——
    // 把它们画成圆角卡通样，用户反而要找一下哪个是关闭。
    //
    // **坐标写在 16 分格里，且必须用 WindowGlyph 画**：最小化就是一条水平线，
    // 外接框高度是 0，`Stretch.Uniform` 算缩放时除以 0 —— 实测那条线会缩成一个点。
    // 别的图标走 24 分格 + 等比拉伸，这一组走 16 分格 + 不拉伸。

    /// <summary>最小化：一条水平线。</summary>
    public const string Minimize = "M3.5,8 H12.5";

    /// <summary>最大化：一个方框。</summary>
    public const string Maximize = "M3.5,3.5 H12.5 V12.5 H3.5 Z";

    /// <summary>还原：前框 + 后框的一角，两框错开 2.5。</summary>
    public const string Restore = "M3,6 H10.5 V13 H3 Z M5.5,6 V3.5 H13 V10.5 H10.5";

    /// <summary>关闭：一个叉。</summary>
    public const string Close = "M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5";

    /// <summary>窗口按钮那一组：按 16 分格原样画，不做等比拉伸（原因见上面那段注释）。</summary>
    public static Path WindowGlyph(string geometry, double size, IBrush stroke) => new()
    {
        Data = Geometry.Parse(geometry),
        Stroke = stroke,
        StrokeThickness = 1.1,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
        Stretch = Stretch.None,
        Width = size,
        Height = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// 应用标记：程序图标那张"蓝底 + 两张白纸 + 琥珀拉头"的矢量版，用在自绘标题栏最左边。
    /// <para>与 <c>assets/make_icon.py</c> 是同一套构图、两套实现：那边是打进 exe 的位图
    /// （要喂给资源管理器、任务栏、右键菜单），这里是窗口内要跟着主题重画的矢量。
    /// 两者共用 24 分格与同一族令牌，改任何一边都要记得另一边 —— 规范 6.4 末尾写着这件事。</para>
    /// </summary>
    public static Control AppMark(double size)
    {
        var unit = size / 24.0;

        static Border Piece(double u, double x, double y, double w, double h, double r, IBrush fill) => new()
        {
            Width = w * u,
            Height = h * u,
            CornerRadius = new Avalonia.CornerRadius(r * u),
            Background = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(x * u, y * u, 0, 0),
        };

        var tile = Piece(unit, 0, 0, 24, 24, 5.4, Design.Brand.Base);
        tile.Child = new Panel
        {
            Children =
            {
                Piece(unit, 5.5, 6, 5.5, 12, 1.4, Design.Ink.OnBrand),
                Piece(unit, 13, 6, 5.5, 12, 1.4, Design.Ink.OnBrand),
                Piece(unit, 9.8, 13.2, 4.4, 5, 1.8, Design.Icon.Face),
            },
        };
        return tile;
    }

    /// <summary>一条线稿图标。尺寸只允许规范 6.3 的那几档。</summary>
    public static Path Line(string geometry, double size, IBrush stroke, double? thickness = null) => new()
    {
        Data = Geometry.Parse(geometry),
        Stroke = stroke,
        StrokeThickness = thickness ?? Stroke,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        // 描边有一半会画到视框外，留出来才不会把圆头切平。
        Margin = new Avalonia.Thickness(size * 0.06),
    };

    /// <summary>内容区的文件夹：面 + 边，同一形状画两遍（填充不带描边会显脏）。</summary>
    public static Control Folder(double size, IBrush? face = null, IBrush? edge = null) => new Path
    {
        Data = Geometry.Parse(FolderGeometry),
        Fill = face ?? Design.Icon.Face,
        Stroke = edge ?? Design.Icon.Edge,
        StrokeThickness = size >= 20 ? 1.2 : 1,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size * 0.85,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>内容区的文件：线稿，与文件夹同一重量。</summary>
    public static Control File(double size, IBrush? stroke = null)
        => Line(FileGeometry, size, stroke ?? Design.Icon.Glyph, size <= Design.IconSize.Row ? 1.2 : Stroke);
}
