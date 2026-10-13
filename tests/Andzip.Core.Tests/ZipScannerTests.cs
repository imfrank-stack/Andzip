using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// ZIP 骨架扫描器的契约。它是修复功能的底座，所以这里的断言都在问同一类问题：
/// 「中央目录没了的时候，你还知道磁盘上有什么吗」——猜错位置的搬运比不搬更糟。
/// </summary>
public class ZipScannerTests : IDisposable
{
    /// <summary>局部文件头签名。测试里自己写一遍而不是引用生产常量：抄来的常量验不出读反了字节序。</summary>
    private const uint LocalHeaderSignature = 0x04034b50;

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-zipscan-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>造一个正常 zip。<see cref="ZipArchive"/> 写 UTF-8 名字时会置 flags bit 11，
    /// 正是中文成员名要验的那一条。</summary>
    private string BuildZip(params (string Name, byte[] Data)[] entries)
    {
        var path = PathOf("box.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return path;
    }

    private static byte[] Noise(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---------------------------------------------------------------- 正常包

    [Fact]
    public void ReadsBothViewsOfAHealthyArchive()
    {
        var zip = BuildZip(
            ("readme.txt", "hello"u8.ToArray()),
            ("docs/说明.md", "中文内容，不是 ASCII。"u8.ToArray()),
            ("big.bin", Noise(1, 300_000)));

        var map = ZipScanner.Inspect(zip);

        Assert.Equal(0, map.PrefixLength);
        Assert.True(map.EocdOffset > 0);
        Assert.Equal(3, map.CentralDirectory.Count);
        Assert.Equal(3, map.LocalHeaders.Count);
        Assert.Empty(map.Notes);
        Assert.Equal(0, map.MissingCentralEntries);

        // 中央目录与局部头必须指向同一个位置，否则"原样搬运"就是搬错地方。
        foreach (var entry in map.CentralDirectory)
        {
            Assert.NotNull(map.LocalAt(map.AbsoluteLocalOffset(entry)));
        }

        Assert.Contains("docs/说明.md", map.CentralDirectory.Select(c => c.Name));
    }

    [Fact]
    public void SizesMatchWhatWasPutIn()
    {
        var payload = Noise(7, 123_456);
        var zip = BuildZip(("noise.bin", payload));

        var entry = Assert.Single(ZipScanner.Inspect(zip).CentralDirectory);

        Assert.Equal(payload.LongLength, entry.UncompressedSize);
        Assert.Equal(8, entry.Method);

        // 随机数据压不动，Deflate 在这种输入上可以比原文「更大」几个字节 —— 第一版断言写成
        // "必须更小"，是被自己的直觉骗了。这里只要求它是个说得过去的长度。
        Assert.InRange(entry.CompressedSize, 1, payload.LongLength + 4096);
    }

    // -------------------------------------------------- 中央目录没了还能做什么

    [Fact]
    public void WalksLocalHeadersWhenTheCentralDirectoryIsGone()
    {
        // 这是模式一的用武之地：中央目录坏了，成员数据一个没丢。
        var zip = BuildZip(
            ("a.txt", "aaa"u8.ToArray()),
            ("b/c.txt", "bbb"u8.ToArray()),
            ("d.txt", "ddd"u8.ToArray()));

        var map = ZipScanner.Inspect(zip);
        Assert.True(map.CentralDirectoryLength > 0, "先确认正常包里能读出中央目录范围，否则下面抹的是空气");

        // 只抹中央目录那一段，成员数据一个字节都不动 —— 上一版按猜的范围抹，
        // 把局部头一起抹掉了，于是"扫描器找不到成员"这个假结论被测成了真的。
        var bytes = File.ReadAllBytes(zip);
        Array.Clear(bytes, (int)map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);
        var wiped = PathOf("wiped.zip");
        File.WriteAllBytes(wiped, bytes);

        var rescan = ZipScanner.Inspect(wiped);

        // 中央目录读不出来了，但三个局部头必须被游走找回来，名字一个不少。
        Assert.Empty(rescan.CentralDirectory);
        Assert.Equal(3, rescan.LocalHeaders.Count);
        Assert.Equal(["a.txt", "b/c.txt", "d.txt"], rescan.LocalHeaders.Select(h => h.Name).ToArray());
        Assert.Contains(rescan.Notes, n => n.Contains("中央目录"));
    }

    [Fact]
    public void ReportsTruncationInsteadOfPretendingToBeWhole()
    {
        var zip = BuildZip(("first.bin", Noise(2, 200_000)), ("second.bin", Noise(3, 200_000)));

        var bytes = File.ReadAllBytes(zip);
        var cut = PathOf("cut.zip");
        File.WriteAllBytes(cut, bytes.AsSpan(0, bytes.Length * 3 / 5));

        var map = ZipScanner.Inspect(cut);

        // 中央目录没了（被截掉了）—— 必须有一条说明，而不是安静地返回空表。
        Assert.True(map.CentralDirectory.Count == 0 || map.MissingCentralEntries > 0);
        Assert.NotEmpty(map.Notes);

        // 第一个成员数据完整，第二个只剩一半：能不能原样搬走是这两种命运的分界。
        var complete = map.LocalHeaders.Count(h => h.IsDataComplete(map.FileLength));
        Assert.Equal(1, complete);
        Assert.Equal(2, map.LocalHeaders.Count);
    }

    [Fact]
    public void ResolvesAnSfxPrefixInsteadOfOffByNCopying()
    {
        var zip = BuildZip(("payload.txt", "inside the archive"u8.ToArray()));
        var original = File.ReadAllBytes(zip);

        // 前导里故意放两个假局部头签名：偏移换算一错，这些就会被当成成员。
        var prefix = new byte[4096];
        new Random(11).NextBytes(prefix);
        prefix[100] = (byte)'P';
        prefix[101] = (byte)'K';
        prefix[102] = 3;
        prefix[103] = 4;
        prefix[2000] = (byte)'P';
        prefix[2001] = (byte)'K';
        prefix[2002] = 3;
        prefix[2003] = 4;

        var sfx = PathOf("setup.exe");
        using (var write = File.Create(sfx))
        {
            write.Write(prefix);
            write.Write(original);
        }

        var map = ZipScanner.Inspect(sfx);

        Assert.Equal(prefix.LongLength, map.PrefixLength);
        var entry = Assert.Single(map.CentralDirectory);
        Assert.Equal("payload.txt", entry.Name);

        // 真正的判据：按换算后的位置在文件里能找到那个局部头，而且前导里的假签名一个都没被收。
        var absolute = map.AbsoluteLocalOffset(entry);
        var onDisk = File.ReadAllBytes(sfx);
        Assert.Equal(LocalHeaderSignature,
            BinaryPrimitives.ReadUInt32LittleEndian(onDisk.AsSpan((int)absolute, 4)));
        Assert.NotNull(map.LocalAt(absolute));
        Assert.DoesNotContain(map.LocalHeaders, h => h.HeaderOffset < map.PrefixLength);
    }

    // ------------------------------------------------------------ 假签名抗性

    [Fact]
    public void DoesNotInventMembersOutOfNoise()
    {
        // 纯随机数据里出现 PK\x03\x04 是概率事件，出现"像文件名的名字"几乎不可能。
        // 这一条挡住的正是 SFX 引导程序和被覆盖区域里的字节。
        var junk = PathOf("junk.bin");
        File.WriteAllBytes(junk, Noise(99, 400_000));

        var map = ZipScanner.Inspect(junk);

        Assert.Empty(map.LocalHeaders);
        Assert.Empty(map.CentralDirectory);
    }

    // ----------------------------------------------------------------- ZIP64

    [Fact]
    public void ReadsZip64ExtraFieldsInTheirRealOrder()
    {
        // ZIP64 扩展只写"被 0xFFFFFFFF 顶掉的那几个"字段，顺序是 未压缩 → 压缩 → 相对位置。
        // 顺序读错一位会把大小当偏移用，而且看起来是成功的 —— 所以要拿手搭的字节验一遍。
        var zip = PathOf("z64.zip");
        File.WriteAllBytes(zip, BuildZip64Fixture());

        var map = ZipScanner.Inspect(zip);

        var entry = Assert.Single(map.CentralDirectory);
        Assert.True(entry.NeedsZip64);
        Assert.Equal(5_000_000_000L, entry.UncompressedSize);
        Assert.Equal(4_000_000_000L, entry.CompressedSize);
        Assert.Equal(0, entry.LocalHeaderOffset);
    }

    /// <summary>手搭一个最小 ZIP64：一个局部头 + 一条中央目录 + ZIP64 EOCD 三件套。</summary>
    private static byte[] BuildZip64Fixture()
    {
        using var ms = new MemoryStream();

        void U16(ushort v) => Write(ms, BitConverter.GetBytes(v));
        void U32(uint v) => Write(ms, BitConverter.GetBytes(v));
        void U64(ulong v) => Write(ms, BitConverter.GetBytes(v));

        var name = Encoding.UTF8.GetBytes("huge.bin");

        // 局部头：两个大小都是占位符，扩展里按序给未压缩、压缩两个 8 字节值。
        U32(0x04034b50);
        U16(45); U16(0x000 | 0x800); U16(8); U16(0); U16(0); U32(0xDEADBEEF);
        U32(uint.MaxValue); U32(uint.MaxValue);
        U16((ushort)name.Length); U16(20);
        Write(ms, name);
        U16(0x0001); U16(16); U64(5_000_000_000UL); U64(4_000_000_000UL);

        var localEnd = ms.Position;
        Write(ms, new byte[64]);   // 装意思一下的"载荷"

        // 中央目录：三个字段全用占位符，扩展顺序必须是 未压缩 → 压缩 → 偏移。
        var cdAt = ms.Position;
        U32(0x02014b50);
        U16(45); U16(45); U16(0x800); U16(8); U16(0); U16(0); U32(0xDEADBEEF);
        U32(uint.MaxValue); U32(uint.MaxValue);
        U16((ushort)name.Length); U16(28); U16(0); U16(0); U16(0); U32(0); U32(0);
        Write(ms, name);
        U16(0x0001); U16(24); U64(5_000_000_000UL); U64(4_000_000_000UL); U64((ulong)localEnd);

        // ZIP64 EOCD 记录 + 定位器 + EOCD，全部按规范尺寸。
        var z64EocdAt = ms.Position;
        U32(0x06064b50); U64(44); U16(45); U16(45); U32(0); U32(0);
        U64(1); U64(1); U64((ulong)(ms.Position - cdAt)); U64((ulong)cdAt);

        var locatorAt = ms.Position;
        U32(0x07064b50); U32(0); U64((ulong)z64EocdAt); U32(1);

        U32(0x06054b50); U16(0); U16(0); U16(0xFFFF); U16(0xFFFF); U32(0xFFFFFFFF); U32(uint.MaxValue); U16(0);
        _ = locatorAt;

        return ms.ToArray();
    }

    private static void Write(Stream stream, ReadOnlySpan<byte> bytes) => stream.Write(bytes);

    // ------------------------------------------------------- "有没有损伤"这个判断
    //
    // 它是修复入口的依据：判成"有问题"就去问用户选哪种模式，判成"没问题"就一句话收工。
    // 所以两边都要钉住 —— 把好包说成坏包（SFX、ZIP64 是最容易被误判的两类）
    // 与把坏包说成好包一样糟。

    [Fact]
    public void AWholeArchiveIsNotReportedAsDamaged()
    {
        var zip = BuildZip(
            ("readme.txt", "hello"u8.ToArray()),
            ("docs/说明.md", "中文内容"u8.ToArray()));

        Assert.False(ZipScanner.Inspect(zip).Damaged);
    }

    [Fact]
    public void AnSfxPrefixIsInformationNotDamage()
    {
        var zip = BuildZip(("payload.txt", "inside"u8.ToArray()));
        var sfx = PathOf("sfx-info.exe");
        using (var write = File.Create(sfx))
        {
            write.Write(Noise(5, 4096));
            write.Write(File.ReadAllBytes(zip));
        }

        var map = ZipScanner.Inspect(sfx);

        // 先确认扫描器真的把前导当情报报了一句 —— 否则这条测的是"没读到东西"。
        Assert.Contains(map.Notes, n => n.Contains("自解压前导"));
        Assert.False(map.Damaged, "前导只是偏移换算的说明，不是结构损伤：拿它去问用户修不修就是误报");
    }

    [Fact]
    public void AHandBuiltZip64IsNotReportedAsDamaged()
    {
        var zip = PathOf("z64-health.zip");
        File.WriteAllBytes(zip, BuildZip64Fixture());

        Assert.False(ZipScanner.Inspect(zip).Damaged);
    }

    [Fact]
    public void MissingEndRecordCountsAsDamage()
    {
        var zip = BuildZip(("first.bin", Noise(2, 200_000)), ("second.bin", Noise(3, 200_000)));
        var bytes = File.ReadAllBytes(zip);
        var cut = PathOf("cut-damaged.zip");
        File.WriteAllBytes(cut, bytes.AsSpan(0, bytes.Length * 3 / 5));

        Assert.True(ZipScanner.Inspect(cut).Damaged);
    }

    [Fact]
    public void WipedCentralDirectoryCountsAsDamage()
    {
        var zip = BuildZip(("a.txt", "aaa"u8.ToArray()), ("b.txt", "bbb"u8.ToArray()));
        var map = ZipScanner.Inspect(zip);

        var bytes = File.ReadAllBytes(zip);
        Array.Clear(bytes, (int)map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);
        var wiped = PathOf("wiped-damaged.zip");
        File.WriteAllBytes(wiped, bytes);

        var rescan = ZipScanner.Inspect(wiped);

        // 成员数据一个没丢、局部头全在，但声称的条数与实际读出的对不上 —— 这正是模式一的用武之地。
        Assert.Equal(2, rescan.LocalHeaders.Count);
        Assert.True(rescan.Damaged);
    }

    [Fact]
    public void MissingFileIsNotFoundNotScanned()
    {
        Assert.Throws<FileNotFoundException>(() => ZipScanner.Inspect(PathOf("nope.zip")));
    }
}
