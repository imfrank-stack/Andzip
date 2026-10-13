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

    /// <summary>打开：文件夹轮廓 + 向右箭头（箭头不压住文件夹上沿）。</summary>
    public const string Open =
        "M3,9 Q3,7 5,7 H8.5 L10.5,9.5 H19 Q21,9.5 21,11.5 V19 Q21,21 19,21 H5 Q3,21 3,19 Z "
        + "M9.5,15 H14 M12,12.5 L14.5,15 12,17.5";

    /// <summary>解压：抽屉 + 向下箭头穿过上沿 + 拉手。</summary>
    public const string Extract =
        "M3,13 Q3,11 5,11 H19 Q21,11 21,13 V19 Q21,21 19,21 H5 Q3,21 3,19 Z "
        + "M10,16 H14 M12,2.5 V9.5 M9,7 L12,10 15,7";

    /// <summary>新建：带拉链的压缩包 —— 本软件的身份图标，齿与扣环都保留。</summary>
    public const string New =
        "M5,4 Q5,2 7,2 H17 Q19,2 19,4 V20 Q19,22 17,22 H7 Q5,22 5,20 Z "
        + "M12,2 V16.5 M9.5,5.5 H12 M12,8.5 H14.5 M9.5,11.5 H12 M12,14 H14.5 "
        + "M10,17 H14 V20.5 H10 Z";

    /// <summary>添加：文件 + 折角，加号落在文件外右下，不与折角打架。</summary>
    public const string Add =
        "M4,4 Q4,2 6,2 H11 L16,7 V13 M4,4 V20 Q4,22 6,22 H9 M11,2 V6 Q11,7 12,7 H16 "
        + "M18.5,15 V21.5 M15.5,18.5 H21.5";

    /// <summary>删除：同样的文件，减号。</summary>
    public const string Delete =
        "M4,4 Q4,2 6,2 H11 L16,7 V13 M4,4 V20 Q4,22 6,22 H9 M11,2 V6 Q11,7 12,7 H16 "
        + "M15.5,18.5 H21.5";

    /// <summary>测试：闪电。描边而不是实心 —— 与其他线稿同一视觉重量。</summary>
    public const string Test = "M13.5,2.5 L6.5,13 H11 L9.5,21.5 17.5,11 H13 Z";

    /// <summary>扫描：盾牌 + 对勾。</summary>
    public const string Scan =
        "M12,2.5 L4.5,5.5 V12 Q4.5,17.5 12,21.5 Q19.5,17.5 19.5,12 V5.5 Z "
        + "M8.5,11.5 L11.3,14.3 15.9,9.2";

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
