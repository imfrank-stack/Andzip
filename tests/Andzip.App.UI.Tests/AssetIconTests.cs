using System.IO;

namespace Andzip.App.UiTests;

/// <summary>
/// 程序图标这个<strong>文件</strong>的把关：它不是一段代码，改坏了编译器不知道，
/// 只有用户某天看到资源管理器里一片空白才知道。所以把 ICO 的结构直接解析一遍来断言。
/// <para>
/// 钉住的是三件事：七档尺寸齐全（缺 16/24/32 的话 shell 会自己去缩 256，糊）、
/// 小的排在后面而大的排在前面（shell 取第一个能用的）、以及被测试的这份就是
/// 打进 exe 的那一份（<c>&lt;ApplicationIcon&gt;</c> 指向同一个路径）。
/// </para>
/// </summary>
public class AssetIconTests
{
    private static readonly int[] Required = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>16/24/32 必须是无压缩位图：Office 那一类老组件读 PNG 条目的 16px 会读成空白。</summary>
    private static readonly int[] BitmapEntries = [16, 24, 32];

    [Fact]
    public void IconCarriesEverySizeFromSixteenToTwoFiftySix()
    {
        var entries = ReadEntries(out _);

        Assert.Equal(Required, entries.Select(e => e.Width).OrderBy(s => s));
        Assert.All(entries, entry => Assert.Equal(32, entry.BitCount));
    }

    [Fact]
    public void IconDirectoryListsTheLargestImageFirst()
    {
        // shell 是从前往后挑第一个能用的：小的排前面会让它拿 16 去撑 48 的位置。
        var entries = ReadEntries(out _);
        var areas = entries.Select(e => (long)e.Width * e.Height).ToList();

        Assert.True(areas.SequenceEqual(areas.OrderByDescending(a => a)),
            $"ICO 目录不是按面积从大到小排的：{string.Join(",", areas)}");
    }

    [Fact]
    public void SmallEntriesAreBitmapsWhileTheLargeOnesArePng()
    {
        var entries = ReadEntries(out var bytes);

        foreach (var entry in entries)
        {
            var isPng = bytes[entry.Offset] == 0x89 && bytes[entry.Offset + 1] == (byte)'P';
            var expectedPng = !BitmapEntries.Contains(entry.Width);
            Assert.True(isPng == expectedPng,
                $"{entry.Width}px 存成了 {(isPng ? "PNG" : "DIB")}，规范 6.4 要求的是"
                + $"{(expectedPng ? "PNG" : "DIB")}");
        }
    }

    [Fact]
    public void TheIconUnderTestIsTheOneThatShipsInTheExecutable()
    {
        var project = Path.Combine(RepositoryRoot(), "src", "Andzip.App", "Andzip.App.csproj");
        var text = File.ReadAllText(project);

        Assert.Contains("assets\\andzip.ico", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LargestImageIsTrueColorPngWithATransparentMargin()
    {
        // 只验"这还是一张 32 位 RGBA、四角透明的大图"，不验主色：
        // 换设计时这条会拦住"导出成了带白底的 24 位图"，而不会拦每一次微调。
        var entries = ReadEntries(out var bytes);
        var png = entries.Single(e => e.Width == 256);

        Assert.Equal(0x89, bytes[png.Offset]);
        var bitDepth = bytes[png.Offset + 24];
        var colorType = bytes[png.Offset + 25];
        Assert.Equal(8, bitDepth);
        Assert.Equal(6, colorType);   // 6 = 真彩色 + alpha，缺 alpha 的图标在深色任务栏上会带一圈白底
    }

    /// <summary>
    /// 小尺寸那三档必须<strong>真的</strong>带透明像素，而不是把透明合成到白底上再存一遍。
    /// <para>这是"桌面上的图标带透明通道"这句话唯一可验证的形式：shell 画 16/24/32 时读的就是
    /// 这几档，它们要是白底，桌面上就会看到一个方方的白块，而 exe 里的 256 档再透明也没用。</para>
    /// </summary>
    [Fact]
    public void SmallBitmapEntriesKeepTransparentCorners()
    {
        var entries = ReadEntries(out var bytes);

        foreach (var entry in entries.Where(e => BitmapEntries.Contains(e.Width)))
        {
            var size = entry.Width;

            // 头 40 字节，然后是自下而上的 BGRA；高度字段是"颜色 + 掩码"的两倍
            Assert.Equal(40, BitConverter.ToInt32(bytes, entry.Offset));
            Assert.Equal(size * 2, BitConverter.ToInt32(bytes, entry.Offset + 8));

            var stride = size * 4;
            var firstRow = entry.Offset + 40;
            var lastRow = firstRow + stride * (size - 1);

            var cornerAlpha = bytes[lastRow + 3];           // 左上角（存储顺序的最后一段）
            var oppositeAlpha = bytes[firstRow + (size - 1) * 4 + 3];

            Assert.True(cornerAlpha == 0 && oppositeAlpha == 0,
                $"{size}px 那档的四角 alpha 不是 0（{cornerAlpha} / {oppositeAlpha}）—— "
                + "桌面上会带一个方方的白底");
        }
    }

    /// <summary>
    /// 图源就是那张带透明通道的 PNG，而界面里用的图标与 exe 里的是<strong>同一张</strong>。
    /// <para>标题栏那枚、任务栏那个、桌面上那个一旦来自三个源，改设计时必然出现"对不上"。</para>
    /// </summary>
    [Fact]
    public void ThePngSourceIsEmbeddedAndActuallyTransparent()
    {
        var path = Path.Combine(RepositoryRoot(), "assets", "andzip.png");
        Assert.True(File.Exists(path), "assets/andzip.png 不见了：ICO 与界面图标都以它为准。");

        // 直接读 PNG 的 IHDR：签名 + 位深 8 + 颜色类型 6（真彩色带 alpha）。
        // 不解码像素 —— 透明这件事在文件头就定了，而读文件头不需要把 Avalonia 拉进来。
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal(8, bytes[24]);
        Assert.Equal(6, bytes[25]);

        var project = Path.Combine(RepositoryRoot(), "src", "Andzip.App", "Andzip.App.csproj");
        Assert.Contains("assets\\andzip.png", File.ReadAllText(project), StringComparison.Ordinal);
    }

    private sealed record Entry(int Width, int Height, int BitCount, int Length, int Offset);

    private static List<Entry> ReadEntries(out byte[] bytes)
    {
        bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "assets", "andzip.ico"));
        var data = bytes;

        Assert.True(data.Length > 6, "ICO 文件是空的");
        Assert.Equal(0, BitConverter.ToUInt16(data, 0));      // Reserved
        Assert.Equal(1, BitConverter.ToUInt16(data, 2));      // Type = 1（图标）
        var count = BitConverter.ToUInt16(data, 4);

        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = 6 + 16 * i;
            entries.Add(new Entry(
                data[at] == 0 ? 256 : data[at],
                data[at + 1] == 0 ? 256 : data[at + 1],
                BitConverter.ToUInt16(data, at + 6),
                (int)BitConverter.ToUInt32(data, at + 8),
                (int)BitConverter.ToUInt32(data, at + 12)));
        }

        Assert.All(entries, entry =>
        {
            Assert.True(entry.Offset + entry.Length <= data.Length,
                $"{entry.Width}px 那条的数据越界了");
            Assert.Equal(entry.Width, entry.Height);
        });
        return entries;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Andzip.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 往上找不到仓库根。");
    }
}
