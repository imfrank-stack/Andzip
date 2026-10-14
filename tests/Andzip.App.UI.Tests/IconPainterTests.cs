using System.IO;
using Andzip.Core.Iconography;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

namespace Andzip.App.UiTests;

/// <summary>
/// 离屏画出来的那一枚图标，到底是不是用户挑的颜色。
/// <para>这里量的不是"代码里调了 <c>DrawGeometry</c>"，而是<strong>产物字节</strong>：
/// PNG 解码回来数像素、ICO 的头逐条对。配色这件事错了就是屏幕上那枚图标红得不像话，
/// 而属性读回来全都对。</para>
/// </summary>
[Collection("界面状态")]
public class IconPainterTests
{
    /// <summary>前层涂纯红、后层涂纯绿：一眼分得清哪块是哪块，也一眼分得清红蓝有没有互换。</summary>
    private static readonly IconPalette RedGreen =
        new("#FF0000", "#FF0000", "#FF0000", "#00FF00", "#00FF00", 0);

    [AvaloniaFact]
    public void ThePngCarriesTheChosenColoursAndAnEmptyCorner()
    {
        var frame = IconPainter.Frame(IconGlyph.Punched, RedGreen, 256);

        Assert.Equal((byte)0x89, frame.Png[0]);
        Assert.Equal((byte)'P', frame.Png[1]);

        var pixels = Png(frame.Png);
        Assert.Equal(256, pixels.Width);

        var red = 0;
        var green = 0;
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var i = (y * 256 + x) * 4;

                // CopyPixels 给的是 B,G,R,A —— 红在第 3 个字节上。这条断言反过来也钉着字节序：
                // 记成 R,G,B,A 的话这里就是 0 个红像素（第一次跑就是这么红的）。
                var (b, g, r, a) = (pixels.Buffer[i], pixels.Buffer[i + 1], pixels.Buffer[i + 2], pixels.Buffer[i + 3]);

                if (a == 255 && r > 200 && g < 60 && b < 60)
                {
                    red++;
                }

                // 后层是 0.55 不透明度叠在透明上。注意通道值是<strong>预乘过的</strong>：
                // 纯绿 0.55 读回来是 (G=140, A=140) 而不是 (G=255, A=140) —— 按实心绿去判，
                // 这块板子永远"没画出来"（第一次就是这么红的）。
                if (a > 90 && a < 220 && g > 100 && r < 60 && b < 60)
                {
                    green++;
                }
            }
        }

        Assert.True(red > 20_000, $"前层只有 {red} 个红像素 —— 红蓝互换了，或者前层根本没画上去");
        Assert.True(green > 1_000, $"后层只有 {green} 个半透的绿像素 —— 那块退到后面的板子没画出来");

        Assert.True(pixels.Buffer[3] == 0, "画布左上角不透明 —— 图标会带一圈方方的底");
    }

    /// <summary>镂空那一档：字是洞，要真的透出去。</summary>
    [AvaloniaFact]
    public void ThePunchedGlyphIsAHoleAndNotAPaintedWord()
    {
        var pixels = Png(IconPainter.Frame(IconGlyph.Punched, RedGreen, 256).Png);
        Assert.True(TransparentInsideThePlate(pixels) > 200, "板子里没有一个透明像素 —— 字没挖开");

        // 对照：溢出那一档的字是白色画在板子上，洞（透明）只该在裁切之外。
        var overflow = Png(IconPainter.Frame(IconGlyph.Overflow, RedGreen, 256).Png);
        Assert.True(TransparentInsideThePlate(overflow) < 200,
            "溢出那一档的板子中间也漏了 —— 裁切没生效，两档会长得一样");
    }

    /// <summary>
    /// ICO 容器：七档、大的排前面、≤32 是裸位图而 ≥48 是 PNG，256 在目录里记 0。
    /// <para>这三条规矩一条一条对上 <c>assets/make_icon.py</c>，因为 shell 就是按它们挑图的：
    /// 顺序错了桌面会拿到 32 的那档，小尺寸放 PNG 老组件读成空白，256 写成 256 会溢出成 0
    /// 而那一格本来就该是 0 —— 但目录里的宽高与数据里的真实尺寸必须都对得上。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheIcoContainerFollowsTheSameRulesAsTheShippedIcon()
    {
        var ico = IconPainter.Ico(IconGlyph.Punched, RedGreen);

        using var stream = new MemoryStream(ico);
        using var reader = new BinaryReader(stream);

        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.Equal((ushort)7, count);

        var entries = new (int Width, uint Offset, uint Length)[count];
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            reader.ReadBytes(6);
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            Assert.Equal(width, height);
            entries[i] = (width == 0 ? 256 : width, offset, length);
        }

        Assert.Equal(IconPainter.Sizes.OrderByDescending(s => s).ToArray(),
            entries.Select(e => e.Width).ToArray());

        foreach (var (width, offset, length) in entries)
        {
            var isPng = ico[offset] == 0x89;
            Assert.True(width <= 32 ? !isPng : isPng,
                $"{width}px 那一档用了 {(isPng ? "PNG" : "裸位图")}，与 make_icon.py 的规矩相反");

            if (!isPng)
            {
                Assert.Equal(40, BitConverter.ToInt32(ico, (int)offset));         // BITMAPINFOHEADER
                Assert.Equal(width, BitConverter.ToInt32(ico, (int)offset + 4));  // 宽
                Assert.Equal(width * 2, BitConverter.ToInt32(ico, (int)offset + 8)); // 高 = 颜色 + 掩码
            }
        }

        // 16 那档的最后一行（自下而上 => 在数据里是第一行）应当有红：证明字节序与行序都没翻错。
        var smallest = entries[^1];
        var pixels = (int)smallest.Offset + 40;
        var redFound = false;
        for (var i = 0; i < 16 * 16; i++)
        {
            if (ico[pixels + i * 4] < 60 && ico[pixels + i * 4 + 2] > 200 && ico[pixels + i * 4 + 3] == 255)
            {
                redFound = true;
                break;
            }
        }

        Assert.True(redFound, "16px 那档里没有红色（BGRA 里第 2 字节应当是 R）—— 字节序或行序错了");
    }

    private static (int Width, byte[] Buffer) Png(byte[] png)
    {
        var bitmap = new Bitmap(new MemoryStream(png));
        var width = bitmap.PixelSize.Width;
        var buffer = new byte[width * bitmap.PixelSize.Height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, bitmap.PixelSize.Height),
                pinned.AddrOfPinnedObject(), buffer.Length, width * 4);
        }
        finally
        {
            pinned.Free();
        }

        return (width, buffer);
    }

    /// <summary>板子中间那一条带子里有多少个透明像素（字在不在洞里的判据）。</summary>
    private static int TransparentInsideThePlate((int Width, byte[] Buffer) pixels)
    {
        var clear = 0;
        for (var y = (int)(pixels.Width * 0.30); y < pixels.Width * 0.58; y++)
        {
            for (var x = (int)(pixels.Width * 0.28); x < pixels.Width * 0.72; x++)
            {
                if (pixels.Buffer[(y * pixels.Width + x) * 4 + 3] == 0)
                {
                    clear++;
                }
            }
        }

        return clear;
    }
}
