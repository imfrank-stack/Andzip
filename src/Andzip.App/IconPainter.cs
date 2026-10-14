using System.Globalization;
using Andzip.Core.Iconography;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Andzip.App;

/// <summary>
/// 按用户挑的配色把图标离屏画出来：PNG（导出与预览）、BGRA 缓冲与 ICO 容器（装到快捷方式上）。
/// <para>几何与用户给的那个工具一对一（两块圆角板子 + 一排 <c>ZIP</c>），差别只在这里是
/// Avalonia 画布而不是浏览器：文字用 <see cref="FormattedText"/> 排，挖孔用
/// <c>EvenOdd</c> 几何组，裁切用 <c>PushClip</c>，斜体用一条合成矩阵。</para>
/// <para><strong>为什么小尺寸要放大占满</strong>：Windows 是按显示尺寸去挑 ICO 里那一档再缩放的，
/// 而这张图的画板只占 200 格里的 128 格（36..164）。照原样出 16px，桌面上那枚图标就只有约 9 像素宽 ——
/// 与 <c>assets/make_icon.py</c> 同一件事，那里叫 <c>EDGE_FILL</c> / <c>PADDED_FILL</c>，
/// 这里沿用同样的两个数与同样的分界（≤32 与 &gt;32）。</para>
/// </summary>
internal static class IconPainter
{
    /// <summary>与 <c>make_icon.py</c> 同一套档位：少的会让 shell 没有合适的挑，多的白占字节。</summary>
    public static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>画面内容在 200×200 视图里占的那一格（两块板子的并集）。</summary>
    private const double ArtLeft = 36;

    private const double ArtEdge = 128;

    /// <summary>画一帧。返回的 PNG 与 BGRA 是同一帧的两种编码，交给 <see cref="IconArt.Ico"/> 封装。</summary>
    public static IconArt.IconFrame Frame(IconGlyph glyph, IconPalette palette, int size)
    {
        var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));

        using (var context = target.CreateDrawingContext())
        {
            Draw(glyph, palette, size, context);
        }

        using var stream = new MemoryStream();
        target.Save(stream);

        return new IconArt.IconFrame(size, Bgra(target, size), stream.ToArray());
    }

    /// <summary>整份 ICO（七档，大的排前面 —— 排序在 <see cref="IconArt"/> 里做）。</summary>
    public static byte[] Ico(IconGlyph glyph, IconPalette palette)
        => IconArt.Ico(Sizes.Select(size => Frame(glyph, palette, size)).ToList());

    /// <summary>单独导出一张 PNG（界面上那颗「PNG 512」）。</summary>
    public static byte[] Png(IconGlyph glyph, IconPalette palette, int size)
        => Frame(glyph, palette, size).Png;

    private static void Draw(IconGlyph glyph, IconPalette palette, int size, DrawingContext context)
    {
        // 把内容那一格放大到占满这一档（见类型注释），并居中。
        var fill = size <= 32 ? 0.94 : 0.86;
        var scale = size * fill / ArtEdge;
        var centered = (size - ArtEdge * scale) / 2;

        using (context.PushTransform(
                   Matrix.CreateTranslation(-ArtLeft, -ArtLeft) *
                   Matrix.CreateScale(scale, scale) *
                   Matrix.CreateTranslation(centered, centered)))
        {
            var plate = new Rect(36, 36, 112, 112);

            // 后层：同一组方向锚点，半透明，故意露出右下那一角。
            using (context.PushOpacity(0.55))
            {
                context.DrawGeometry(
                    Brush(palette.Back0, palette.Back1, null, palette), null,
                    new RectangleGeometry(new Rect(68, 68, 96, 96), 26, 26));
            }

            var front = Brush(palette.Front0, palette.Front1, palette.Front2, palette);

            if (glyph == IconGlyph.Overflow)
            {
                context.DrawGeometry(front, null, new RectangleGeometry(plate, 31, 31));

                using (context.PushClip(new RoundedRect(plate, 31, 31)))
                {
                    var text = Text("ZIP", 66);

                    // 与 SVG 那条 transform 同一个复合：绕 (92,92) 先放大 1.12 倍、再斜 12°。
                    using (context.PushTransform(
                               Matrix.CreateTranslation(-92, -92) *
                               Matrix.CreateScale(1.12, 1.12) *
                               Matrix.CreateSkew(-12, 0) *
                               Matrix.CreateTranslation(92, 92)))
                    {
                        context.DrawText(text, Placed(text, centreX: 92, baselineY: 112));
                    }
                }
            }
            else
            {
                // 镂空：字是几何上的洞 —— EvenOdd 几何组，第二个孩子把第一个掏空。
                var text = Text("ZIP", 40);
                var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
                group.Children.Add(new RectangleGeometry(plate, 31, 31));
                group.Children.Add(GeometryOf(text, centreX: 92, baselineY: 101));

                context.DrawGeometry(front, null, group);
            }
        }
    }

    /// <summary>
    /// 三段渐变（前层）或两段（后层）。锚点用相对坐标，所以同一份画在 16 与 256 上渐变位置一致。
    /// </summary>
    private static IBrush Brush(string from, string? middle, string? to, IconPalette palette)
    {
        var (x1, y1, x2, y2) = palette.Anchors;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x1 / 100, y1 / 100, RelativeUnit.Relative),
            EndPoint = new RelativePoint(x2 / 100, y2 / 100, RelativeUnit.Relative),
        };

        brush.GradientStops.Add(Stop(from, 0));
        if (middle is not null)
        {
            // 中点固定在 52%：参考工具里那个位置就是这个数，跟着它才叫"一模一样"。
            brush.GradientStops.Add(Stop(middle, 0.52));
        }

        if (to is not null)
        {
            brush.GradientStops.Add(Stop(to, 1));
        }

        return brush;
    }

    private static GradientStop Stop(string hex, double offset)
    {
        var (r, g, b) = IconPalette.Rgb(hex);
        return new GradientStop(Color.FromRgb(r, g, b), offset);
    }

    private static FormattedText Text(string value, double size) => new(
        value,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        // 参考工具用的是 Helvetica Neue / Arial 的 900 字重；这台机器上没有 Helvetica Neue，
        // 落到 Arial 的 Black 上 —— 字形会差一点点，但 SVG 导出里写的仍是原来那一串。
        new Typeface(new FontFamily("Arial"), FontStyle.Normal, FontWeight.Black, FontStretch.Normal),
        size,
        // 这一支画刷其实用不上（要么被转成几何，要么被前层色盖住），但要一个非 null 的：
        // 不写 Design.Ink.OnBrand 而写"白色"会撞上那条"界面零字面色值"的门禁。
        Design.Ink.OnBrand);

    /// <summary>把"居中 + 基线在 y"这套 SVG 文字定位换成画布要的左上角。</summary>
    private static Point Placed(FormattedText text, double centreX, double baselineY)
        => new(centreX - text.Width / 2, baselineY - text.Baseline);

    private static Geometry GeometryOf(FormattedText text, double centreX, double baselineY)
        => text.BuildGeometry(Placed(text, centreX, baselineY))
            ?? throw new InvalidOperationException("这一串字没能转成几何，镂空挖不出洞。");

    /// <summary>
    /// 读回自上而下的 BGRA。
    /// <para><strong>字节序是量出来的</strong>：<c>Bitmap.CopyPixels</c> 给的就是 B,G,R,A，
    /// 与 <c>UiHarness.Pixels</c> 读的那条路<strong>相反</strong>（headless 帧缓冲实测是 R,G,B,A）。
    /// 两个 API 两种顺序，所以这里不换算 —— 换一次就正好换错，ICO 里那枚图标会蓝得不像话。
    /// 这条由 <c>IconPainterTests</c> 用"纯红的前层"钉住：红在每第 3 个字节上。</para>
    /// </summary>
    private static byte[] Bgra(Bitmap bitmap, int size)
    {
        var buffer = new byte[size * size * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), pinned.AddrOfPinnedObject(), buffer.Length, size * 4);
        }
        finally
        {
            pinned.Free();
        }

        return buffer;
    }
}
