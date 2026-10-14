using System.Xml.Linq;
using Andzip.Core.Iconography;

namespace Andzip.Core.Tests;

/// <summary>
/// 图标配色这一档里<strong>不需要画布</strong>的那一半：颜色算式、SVG 文本、ICO 容器。
/// <para>这三样都是纯字节 / 纯字符串，所以能逐字节断言。渲染那一半要 Avalonia，
/// 在界面测试工程里量像素（<c>IconPainterTests</c>）。</para>
/// </summary>
public class IconArtTests
{
    // ------------------------------------------------------------- 颜色算式

    [Theory]
    [InlineData("#4AD9DC", "#4AD9DC")]
    [InlineData("4ad9dc", "#4AD9DC")]
    [InlineData("  #4aD9dC  ", "#4AD9DC")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("4AD9D", null)]          // 少一位
    [InlineData("4AD9DCD", null)]        // 多一位
    [InlineData("#4AD9DX", null)]        // 不是十六进制
    [InlineData("红色", null)]
    public void NormalizeAcceptsOnlyASixDigitHex(string? input, string? expected)
        => Assert.Equal(expected, IconPalette.Normalize(input));

    [Theory]
    [InlineData("#000000", 0.0, "#000000")]
    [InlineData("#000000", 1.0, "#FFFFFF")]
    [InlineData("#000000", 0.5, "#808080")]
    // 掺白是"每个通道各往 255 走一段"，不是把整色乘一个系数：#123456 掺 0.25
    // 得到 #4D6780（18→77、52→103、86→128），乘系数会得到 #152A40 那种越暗越偏色的东西。
    [InlineData("#123456", 0.25, "#4D6780")]
    [InlineData("#FFFFFF", 0.9, "#FFFFFF")]
    public void MixWhiteTowardWhite(string hex, double amount, string expected)
        => Assert.Equal(expected, IconPalette.MixWhite(hex, amount));

    /// <summary>
    /// 「后层跟随前景」的两个数（0.30 / 0.25）与结果，是照参考工具那个网页算的。
    /// <para>期望值不是从这份 C# 里抄的 —— 是用浏览器同一套 <c>Math.round</c> 独立算出来的
    /// （<c>#4AD9DC</c> 掺 0.30 落在 128.3 / 228.4 / <strong>230.5</strong>）。
    /// 那个 <c>.5</c> 就是这条用例存在的理由：C# 默认的 <c>Math.Round</c> 是"五成双"，
    /// 会把 230.5 收成 230（<c>#E6</c>），而工具给的是 231（<c>#E7</c>）。</para>
    /// </summary>
    [Fact]
    public void TheDerivedBackLayerMatchesTheReferenceTool()
    {
        var derived = IconPalette.Default.WithBackFromFront();

        Assert.Equal("#80E4E7", derived.Back0);
        Assert.Equal("#598BEE", derived.Back1);

        // 掺白只会更亮，不会更暗 —— 后层"退到后面"靠的就是这一档亮度差。
        Assert.True(Luma(derived.Back0) > Luma(IconPalette.Default.Front0));
        Assert.True(Luma(derived.Back1) > Luma(IconPalette.Default.Front2));
    }

    [Fact]
    public void EveryPresetIsMadeOfLegalHexAndThereAreEight()
    {
        Assert.Equal(8, IconPalette.Presets.Length);
        Assert.Equal(IconPalette.Default, IconPalette.Presets[0]);

        foreach (var preset in IconPalette.Presets)
        {
            foreach (var hex in new[] { preset.Front0, preset.Front1, preset.Front2, preset.Back0, preset.Back1 })
            {
                Assert.NotNull(IconPalette.Normalize(hex));
            }
        }
    }

    /// <summary>方向被改坏（手改设置文件）时取模，而不是抛 —— 抛的症状是"程序打不开"。</summary>
    [Fact]
    public void AnOutOfRangeDirectionFallsBackInsteadOfThrowing()
    {
        var broken = IconPalette.Default with { Direction = 99 };

        var tooBig = IconPalette.Default with { Direction = 99 };
        var tooSmall = IconPalette.Default with { Direction = -4 };

        Assert.Equal(IconPalette.Directions[^1], tooBig.Anchors);
        Assert.Equal(IconPalette.Directions[0], tooSmall.Anchors);
    }

    // ------------------------------------------------------------------ SVG

    [Fact]
    public void TheOverflowSvgClipsTheWordToThePlate()
    {
        var svg = XDocument.Parse(IconArt.Svg(IconGlyph.Overflow, IconPalette.Default));
        var text = svg.Descendants(XName.Get("text", "http://www.w3.org/2000/svg")).Single();

        Assert.Equal("66", text.Attribute("font-size")!.Value);
        Assert.Equal("translate(92,92) skewX(-12) scale(1.12) translate(-92,-92)", text.Attribute("transform")!.Value);

        // 字要被板子裁住：裁切组必须真的挂在前面那个 clipPath 上。
        var clip = svg.Descendants(XName.Get("clipPath", "http://www.w3.org/2000/svg")).Single();
        Assert.Equal("31", clip.Element(XName.Get("rect", "http://www.w3.org/2000/svg"))!.Attribute("rx")!.Value);
        Assert.Contains(svg.Descendants().Where(e => e.Name.LocalName == "g"),
            g => g.Attribute(XName.Get("clip-path", ""))?.Value == "url(#clip)");
    }

    [Fact]
    public void ThePunchedSvgKnocksTheWordOutOfThePlate()
    {
        var svg = XDocument.Parse(IconArt.Svg(IconGlyph.Punched, IconPalette.Default));
        var ns = "http://www.w3.org/2000/svg";
        var mask = svg.Descendants(XName.Get("mask", ns)).Single();

        // 遮罩里白是不透明、黑是透明 —— 字得是黑的，否则挖不出洞而是糊上一块白。
        Assert.Equal("#fff", mask.Element(XName.Get("rect", ns))!.Attribute("fill")!.Value);
        Assert.Equal("#000", mask.Element(XName.Get("text", ns))!.Attribute("fill")!.Value);
        Assert.Equal("url(#mask)",
            svg.Descendants(XName.Get("rect", ns)).Single(r => r.Attribute("mask") is not null).Attribute("mask")!.Value);
    }

    [Fact]
    public void TheSvgCarriesTheChosenColoursAndTheChosenDirection()
    {
        var palette = new IconPalette("#FF0000", "#00FF00", "#0000FF", "#111111", "#222222", 2);
        var svg = IconArt.Svg(IconGlyph.Overflow, palette);

        Assert.Contains("stop-color=\"#FF0000\"", svg, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#00FF00\"", svg, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#0000FF\"", svg, StringComparison.Ordinal);
        Assert.Contains("offset=\"52%\"", svg, StringComparison.Ordinal);

        // 方向 2 是"从左到右"那一档（↗ 参考工具里的第三颗）。
        Assert.Contains("x1=\"0%\" y1=\"50%\" x2=\"100%\" y2=\"50%\"", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// 前层与后层的渐变锚点<strong>跟着方向一起转</strong> —— 参考工具里那两个渐变是一起改的。
    /// 只转一个的症状是两块板子的光向不一致，看着像两张图叠错了。
    /// </summary>
    [Fact]
    public void BothGradientsTurnTogether()
    {
        var svg = XDocument.Parse(IconArt.Svg(IconGlyph.Overflow, IconPalette.Default with { Direction = 1 }));
        var ns = "http://www.w3.org/2000/svg";
        var gradients = svg.Descendants(XName.Get("linearGradient", ns)).ToList();

        Assert.Equal(2, gradients.Count);
        Assert.All(gradients, g =>
            Assert.Equal(("50%", "0%", "50%", "100%"),
                (g.Attribute("x1")!.Value, g.Attribute("y1")!.Value, g.Attribute("x2")!.Value, g.Attribute("y2")!.Value)));
    }

    // ------------------------------------------------------------------ ICO

    [Fact]
    public void TheIcoOrdersByAreaAndEncodesSmallOnesAsRawBitmaps()
    {
        var frames = new[] { 16, 256, 48, 32 }.Select(size => Frame(size)).ToList();
        var ico = IconArt.Ico(frames);

        Assert.Equal((ushort)1, BitConverter.ToUInt16(ico, 2));                       // 类型：1 = 图标
        var count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal((ushort)4, count);

        var widths = Enumerable.Range(0, count).Select(i => EntryWidth(ico, i)).ToList();
        Assert.Equal(new[] { 256, 48, 32, 16 }, widths);

        var offset = 6 + 16 * count;
        for (var i = 0; i < count; i++)
        {
            Assert.Equal((uint)offset, BitConverter.ToUInt32(ico, 6 + 16 * i + 12));
            var length = (int)BitConverter.ToUInt32(ico, 6 + 16 * i + 8);
            var isPng = ico[offset] == 0x89;

            Assert.True(widths[i] <= 32 ? !isPng : isPng,
                $"{widths[i]}px 那一档的编码方式与规矩相反（小尺寸要裸位图）");

            if (!isPng)
            {
                Assert.Equal(widths[i] * 2, BitConverter.ToInt32(ico, offset + 8));   // 高 = 颜色 + 掩码
            }

            offset += length;
        }

        // 最后一条的末尾正好是文件长度：中间多一块少一块都是坏容器。
        Assert.Equal(ico.Length, offset);
    }

    /// <summary>ICO 里的位图是<strong>自下而上</strong>的，与画布给的方向相反。不翻就是上下颠倒的图标。</summary>
    [Fact]
    public void TheDibEntryStoresItsRowsBottomUp()
    {
        var frame = Frame(16);
        for (var x = 0; x < 16; x++)
        {
            frame.Bgra[x * 4 + 2] = 0xFF;                       // 画布第一行：红（BGRA 的第 3 字节）
            frame.Bgra[(15 * 16 + x) * 4 + 1] = 0xFF;           // 画布最后一行：绿
        }

        var ico = IconArt.Ico([frame]);
        var pixels = 6 + 16 + 40;                               // 头 + 一条目录项 + BITMAPINFOHEADER

        Assert.True(ico[pixels + 1] == 0xFF && ico[pixels + 2] == 0x00,
            "DIB 的第一行不是画布的最后一行 —— 图标会上下颠倒");
        Assert.True(ico[pixels + (15 * 16 * 4) + 2] == 0xFF, "DIB 的最后一行不是画布的第一行");
    }

    [Fact]
    public void AnEmptyOrMissingFrameSetIsRefused()
    {
        Assert.Throws<ArgumentException>(() => IconArt.Ico([]));
        Assert.Throws<ArgumentException>(() => IconArt.Ico([new IconArt.IconFrame(16, new byte[10], [])]));
    }

    private static IconArt.IconFrame Frame(int size)
        => new(size, new byte[size * size * 4], PngStub(size));

    /// <summary>大尺寸那几档放的是 PNG 字节；这里不需要真图，只要开头那个签名是真的。</summary>
    private static byte[] PngStub(int size)
    {
        var bytes = new byte[12];
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 8);
        return bytes;
    }

    private static int ReadType(byte[] ico) => BitConverter.ToUInt16(ico, 2);

    private static int EntryWidth(byte[] ico, int index)
    {
        var width = ico[6 + 16 * index];
        return width == 0 ? 256 : width;
    }

    private static double Luma(string hex)
    {
        var (r, g, b) = IconPalette.Rgb(hex);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
}
