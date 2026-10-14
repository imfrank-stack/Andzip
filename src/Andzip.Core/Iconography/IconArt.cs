using System.Globalization;
using System.Text;

namespace Andzip.Core.Iconography;

/// <summary>
/// 两种字形（与那个参考工具一对一）：
/// <list type="bullet">
/// <item><description><see cref="Overflow"/> = 「04 溢出裁切斜体」：字比板子大，超出部分裁掉，整排向左斜。</description></item>
/// <item><description><see cref="Punched"/> = 「06 镂空负形」：字是从板子上掏空的洞，透出后面的东西。</description></item>
/// </list>
/// </summary>
public enum IconGlyph
{
    Overflow,
    Punched,
}

/// <summary>
/// 图标的两种产物：SVG（矢量文本，导出用）与 ICO（Windows 的图标容器，装到快捷方式上）。
/// <para>这两件事都放在 Core：<strong>纯字节 / 纯字符串，不需要 Avalonia</strong>，所以能在任何机器上
/// 被逐字节断言（渲染那一半要画布，只能在界面测试里量像素，见 <c>IconPainter</c>）。</para>
/// </summary>
public static class IconArt
{
    /// <summary>
    /// 一张 512×512 的 SVG，结构与用户给的那个工具逐字对齐（同一套锚点、同一个 52% 中点、
    /// 同一个 <c>skewX(-12) scale(1.12)</c>）。
    /// <para>两种字形各用各的 <c>id</c> 前缀：两张图并排放在一个文档里时（设置页的预览就是这个形状），
    /// id 撞了会让第二张的渐变/遮罩指到第一张上 —— 那是"看着对，换一张就不对"的一类 bug。</para>
    /// </summary>
    public static string Svg(IconGlyph glyph, IconPalette palette)
    {
        var (x1, y1, x2, y2) = palette.Anchors;

        static string Percent(double value) =>
            value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"512\" height=\"512\" viewBox=\"0 0 200 200\">\n");
        svg.Append("  <defs>\n");
        svg.Append($"    <linearGradient id=\"front\" x1=\"{Percent(x1)}\" y1=\"{Percent(y1)}\" x2=\"{Percent(x2)}\" y2=\"{Percent(y2)}\">\n");
        svg.Append($"      <stop offset=\"0%\" stop-color=\"{palette.Front0}\"/>\n");
        svg.Append($"      <stop offset=\"52%\" stop-color=\"{palette.Front1}\"/>\n");
        svg.Append($"      <stop offset=\"100%\" stop-color=\"{palette.Front2}\"/>\n");
        svg.Append("    </linearGradient>\n");
        svg.Append($"    <linearGradient id=\"back\" x1=\"{Percent(x1)}\" y1=\"{Percent(y1)}\" x2=\"{Percent(x2)}\" y2=\"{Percent(y2)}\">\n");
        svg.Append($"      <stop offset=\"0%\" stop-color=\"{palette.Back0}\"/>\n");
        svg.Append($"      <stop offset=\"100%\" stop-color=\"{palette.Back1}\"/>\n");
        svg.Append("    </linearGradient>\n");

        if (glyph == IconGlyph.Overflow)
        {
            svg.Append("    <clipPath id=\"clip\"><rect x=\"36\" y=\"36\" width=\"112\" height=\"112\" rx=\"31\"/></clipPath>\n");
        }
        else
        {
            svg.Append("    <mask id=\"mask\" maskUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"200\" height=\"200\">\n");
            svg.Append("      <rect x=\"36\" y=\"36\" width=\"112\" height=\"112\" rx=\"31\" fill=\"#fff\"/>\n");
            svg.Append("      <text x=\"92\" y=\"101\" text-anchor=\"middle\" font-family=\"Helvetica Neue,Arial,sans-serif\" "
                + "font-weight=\"900\" font-size=\"40\" letter-spacing=\"1\" fill=\"#000\">ZIP</text>\n");
            svg.Append("    </mask>\n");
        }

        svg.Append("  </defs>\n");
        svg.Append("  <rect x=\"68\" y=\"68\" width=\"96\" height=\"96\" rx=\"26\" fill=\"url(#back)\" opacity=\"0.55\"/>\n");

        if (glyph == IconGlyph.Overflow)
        {
            svg.Append("  <rect x=\"36\" y=\"36\" width=\"112\" height=\"112\" rx=\"31\" fill=\"url(#front)\"/>\n");
            svg.Append("  <g clip-path=\"url(#clip)\">\n");
            svg.Append("    <text x=\"92\" y=\"112\" text-anchor=\"middle\" font-family=\"Helvetica Neue,Arial,sans-serif\" "
                + "font-weight=\"900\" font-size=\"66\" fill=\"#fff\" "
                + "transform=\"translate(92,92) skewX(-12) scale(1.12) translate(-92,-92)\">ZIP</text>\n");
            svg.Append("  </g>\n");
        }
        else
        {
            svg.Append("  <rect x=\"36\" y=\"36\" width=\"112\" height=\"112\" rx=\"31\" fill=\"url(#front)\" mask=\"url(#mask)\"/>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>
    /// 一帧位图：<paramref name="Size"/> 边长、<paramref name="Bgra"/> 是自上而下的 32 位像素，
    /// <paramref name="Png"/> 是同一帧的 PNG 编码。
    /// <para>为什么要同时给两份：ICO 里小尺寸要放<strong>裸位图</strong>（老的 shell 组件读 PNG 的
    /// 16px 会读成空白，见 <c>assets/make_icon.py</c> 与开发记录第七节第 29 条），
    /// 大尺寸放 PNG 才省字节。编码 PNG 需要画布，所以由界面层备好、这里只负责封装。</para>
    /// </summary>
    public sealed record IconFrame(int Size, byte[] Bgra, byte[] Png);

    /// <summary>
    /// 打成 ICO 容器。<strong>规矩与 <c>assets/make_icon.py</c> 的 <c>write_ico</c> 一对一</strong>：
    /// 边长 ≤32 走 DIB（BITMAPINFOHEADER + 自下而上的 BGRA + 全 0 的 AND 掩码，高度写两倍），
    /// ≥48 走 PNG；256 在目录里记 <c>0x00</c>（那格是一个字节，写 256 会溢出成 0）；
    /// 大的排前面，shell 挑第一个能用的。
    /// </summary>
    public static byte[] Ico(IReadOnlyList<IconFrame> frames)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("一份 ICO 至少要有一帧。", nameof(frames));
        }

        // 面积从大到小：shell 是按顺序取第一个它能用的，小的排前面会让桌面在 256 的位子上拿到 32。
        var ordered = frames.OrderByDescending(f => f.Size * f.Size).ToList();

        var headerSize = 6 + 16 * ordered.Count;
        var blobs = ordered.Select(frame => frame.Size <= 32 ? Dib(frame) : frame.Png).ToList();

        using var output = new MemoryStream();
        var little = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        little.Write((ushort)0);                      // 保留
        little.Write((ushort)1);                      // 类型：1 = 图标
        little.Write((ushort)ordered.Count);

        var offset = headerSize;
        for (var i = 0; i < ordered.Count; i++)
        {
            var edge = ordered[i].Size == 256 ? (byte)0 : (byte)ordered[i].Size;
            little.Write(edge);                       // 宽（0 = 256）
            little.Write(edge);                       // 高（0 = 256）
            little.Write((byte)0);                    // 调色板颜色数
            little.Write((byte)0);                    // 保留
            little.Write((ushort)1);                  // 颜色平面
            little.Write((ushort)32);                 // 位深
            little.Write((uint)blobs[i].Length);
            little.Write((uint)offset);
            offset += blobs[i].Length;
        }

        foreach (var blob in blobs)
        {
            little.Write(blob);
        }

        little.Flush();
        return output.ToArray();
    }

    /// <summary>裸位图那一档：头 + 自下而上的 BGRA + 全 0 的 AND 掩码（每行按 4 字节对齐）。</summary>
    private static byte[] Dib(IconFrame frame)
    {
        var size = frame.Size;
        var pixelBytes = size * size * 4;
        var pixels = frame.Bgra;
        if (pixels is null || pixels.Length < pixelBytes)
        {
            throw new ArgumentException($"这一帧只有 {pixels?.Length ?? 0} 字节，按 {size}×{size} 要 {pixelBytes}。", nameof(frame));
        }

        var andMask = new byte[((size + 31) / 32) * 4 * size];

        using var output = new MemoryStream();
        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        writer.Write((uint)40);                                   // BITMAPINFOHEADER 大小
        writer.Write(size);                                       // 宽
        writer.Write(size * 2);                                   // 高 = 颜色表 + 掩码两张
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)0);                                    // BI_RGB
        writer.Write((uint)(pixelBytes + andMask.Length));
        writer.Write(0);
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        // ICO 里的位图是自下而上的，而画布给的是自上而下 —— 逐行倒着抄。
        var bottomUp = new byte[pixelBytes];
        for (var row = 0; row < size; row++)
        {
            Array.Copy(pixels, (size - 1 - row) * size * 4, bottomUp, row * size * 4, size * 4);
        }

        writer.Write(bottomUp);
        writer.Write(andMask);
        writer.Flush();
        return output.ToArray();
    }
}
