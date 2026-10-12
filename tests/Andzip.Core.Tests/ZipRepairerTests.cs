using System.IO.Compression;
using System.Text;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// ZIP 修复的三种模式。这里最重要的断言不是"修好了几个"，而是那句反向的话：
/// <strong>没把握的东西有没有被老实跳过</strong>，以及<strong>原包有没有被动过</strong>。
/// </summary>
public class ZipRepairerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-repair-").FullName;

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

    private static byte[] Noise(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string BuildZip(string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = PathOf(name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }

    /// <summary>把修好的包读回来，返回「成员名 → 内容」。读不回来就直接失败：
    /// 修复的验收标准必须是别的工具能用，不是我们自己认自己。</summary>
    private static Dictionary<string, byte[]> ReadBack(string archive)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 && entry.FullName.EndsWith('/'))
            {
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[entry.FullName] = buffer.ToArray();
        }

        return result;
    }

    private static void ClearRegion(string file, long offset, int length)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Write);
        stream.Position = offset;
        stream.Write(new byte[length]);
    }

    // ------------------------------------------------------------ 模式一

    [Fact]
    public void RebuildsTheCentralDirectoryFromLocalHeaders()
    {
        var data = (first: Noise(1, 150_000), second: "second member payload"u8.ToArray());
        var zip = BuildZip("healthy.zip", ("a/first.bin", data.first), ("second.txt", data.second));

        var map = ZipScanner.Inspect(zip);
        ClearRegion(zip, map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);

        // 先确认这个残局真的读不出来 —— 否则"修好了"毫无意义。.NET 是在数条目时才发现的，
        // OpenRead 本身不报错，所以断言要包住 .Entries。
        Assert.Throws<InvalidDataException>(() =>
        {
            using var broken = ZipFile.OpenRead(zip);
            _ = broken.Entries.Count;
        });

        var result = ZipRepairer.Repair(zip, ZipRepairMode.RebuildCentralDirectory);

        Assert.True(result.ProducedAnything);
        Assert.Empty(result.SkippedEntries);
        Assert.False(result.OutputPath.Equals(zip, StringComparison.OrdinalIgnoreCase));

        var restored = ReadBack(result.OutputPath);
        Assert.Equal(data.first, restored["a/first.bin"]);
        Assert.Equal(data.second, restored["second.txt"]);
    }

    [Fact]
    public void NeverOverwritesTheOriginalArchive()
    {
        var zip = BuildZip("keep.zip", ("x.bin", Noise(2, 50_000)));
        var before = File.ReadAllBytes(zip);

        Assert.Throws<ArgumentException>(() =>
            ZipRepairer.Repair(zip, ZipRepairMode.RebuildCentralDirectory, outputPath: zip));

        Assert.Equal(before, File.ReadAllBytes(zip));
    }

    [Fact]
    public void RepairedArchiveKeepsOriginalTimestampsInsteadOfNow()
    {
        // 中心目录里写"刚刚修的时间"会让每个成员看起来是今天新建的 —— 那是伪造元数据。
        var zip = BuildZip("stamp.zip", ("old.txt", "content"u8.ToArray()));
        DateTimeOffset original;
        using (var probe = ZipFile.OpenRead(zip))
        {
            // 必须在这里关掉：句柄还开着就没法改写这个文件，下面的抹除会直接报占用。
            original = probe.Entries[0].LastWriteTime;
        }

        var map = ZipScanner.Inspect(zip);
        ClearRegion(zip, map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);

        var result = ZipRepairer.Repair(zip, ZipRepairMode.RebuildCentralDirectory);
        using var repaired = ZipFile.OpenRead(result.OutputPath);

        Assert.Equal(original.LocalDateTime, repaired.Entries[0].LastWriteTime.LocalDateTime,
            precision: TimeSpan.FromSeconds(2));   // DOS 时间戳只有 2 秒精度
    }

    [Fact]
    public void ReportsNothingRecoveredWhenThereAreNoLocalHeaders()
    {
        var junk = PathOf("junk.zip");
        File.WriteAllBytes(junk, Noise(3, 400_000));

        var result = ZipRepairer.Repair(junk, ZipRepairMode.RebuildCentralDirectory);

        Assert.False(result.ProducedAnything);
        Assert.NotEmpty(result.Notes);
        Assert.False(File.Exists(result.OutputPath), "一个成员都没认出来时不该留下一个空壳新包");
    }

    // ------------------------------------------------------------ 模式二

    [Fact]
    public void TruncatedTailIsSkippedByModeOneButKeptByModeTwo()
    {
        // 两种模式的真正分界线：残缺的最后一个成员收不收。
        var zip = BuildZip("cut.zip", ("first.bin", Noise(4, 300_000)), ("last.bin", Noise(5, 300_000)));
        var bytes = File.ReadAllBytes(zip);
        var truncated = PathOf("truncated.zip");
        File.WriteAllBytes(truncated, bytes.AsSpan(0, bytes.Length * 3 / 5));

        var strict = ZipRepairer.Repair(truncated, ZipRepairMode.RebuildCentralDirectory);
        var joined = ZipRepairer.Repair(truncated, ZipRepairMode.JoinTruncatedParts);

        // 残缺成员：模式一不搬（它没法保证搬出来的东西能用），模式二搬并明说。
        Assert.Contains("last.bin", strict.SkippedEntries);
        Assert.DoesNotContain("last.bin", joined.SkippedEntries);
        Assert.Contains(joined.Notes, n => n.Contains("截断"));

        // 但两种模式下第一个成员都必须完好。
        Assert.Equal(Noise(4, 300_000), ReadBack(strict.OutputPath)["first.bin"]);
        Assert.Equal(Noise(4, 300_000), ReadBack(joined.OutputPath)["first.bin"]);
    }

    [Fact]
    public void JoinsSplitVolumesIntoOneWorkingArchive()
    {
        var zip = BuildZip("split.zip", ("one.bin", Noise(6, 200_000)), ("two.bin", Noise(7, 200_000)));
        var bytes = File.ReadAllBytes(zip);

        // 合成一套分卷：前段成 .z01，后段成 .zip（中心目录就在后段里，与真实分卷同构）。
        var cut = bytes.Length / 2;
        File.WriteAllBytes(PathOf("split.z01"), bytes.AsSpan(0, cut));
        File.WriteAllBytes(PathOf("split_parts.zip"), bytes.AsSpan(cut));

        // 后段单独存在时没有中心目录可找（它指向的位置在前段里）—— 正是"分卷必须一起看"。
        var parts = ZipRepairer.FindVolumeParts(PathOf("split_parts.zip"));
        Assert.Single(parts);   // 名字对不上（stem 不同）时不该把无关文件当成分卷

        // 用同名的一套验续接：base.z01 + base.zip
        File.WriteAllBytes(PathOf("base.z01"), bytes.AsSpan(0, cut));
        File.WriteAllBytes(PathOf("base.zip"), bytes.AsSpan(cut));

        var result = ZipRepairer.Repair(PathOf("base.zip"), ZipRepairMode.JoinTruncatedParts);
        Assert.Equal(2, ZipRepairer.FindVolumeParts(PathOf("base.zip")).Count);

        var restored = ReadBack(result.OutputPath);
        Assert.Equal(Noise(6, 200_000), restored["one.bin"]);
        Assert.Equal(Noise(7, 200_000), restored["two.bin"]);
    }

    // ------------------------------------------------------------ 模式三

    [Fact]
    public void SalvageKeepsWhatDecodesAndDropsWhatDoesNot()
    {
        // 坏一个成员时，好的那几个必须还在；而坏的那个必须被点名，不能被静默写成"空文件"。
        var zip = BuildZip("mixed.zip",
            ("good1.bin", Noise(8, 120_000)),
            ("bad.bin", Noise(9, 120_000)),
            ("good2.bin", Noise(10, 120_000)));

        var map = ZipScanner.Inspect(zip);
        var bad = map.LocalHeaders.Single(h => h.Name == "bad.bin");
        var bytes = File.ReadAllBytes(zip);
        bytes[(int)(bad.DataOffset + bad.CompressedSize / 2)] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        var result = ZipRepairer.Repair(zip, ZipRepairMode.SalvageAndRepack);

        Assert.Contains("bad.bin", result.SkippedEntries);
        Assert.Contains("good1.bin", result.RecoveredEntries);
        Assert.Contains("good2.bin", result.RecoveredEntries);

        var restored = ReadBack(result.OutputPath);
        Assert.Equal(Noise(8, 120_000), restored["good1.bin"]);
        Assert.Equal(Noise(10, 120_000), restored["good2.bin"]);
        Assert.False(restored.ContainsKey("bad.bin"));
    }

    [Fact]
    public void ModeThreeAlsoRecoversAnArchiveTheEngineCannotListAtAll()
    {
        // 中心目录整段抹掉时引擎列不出成员；模式三是"先补结构再逐成员"，所以这条能过 ——
        // 它验的是两个模式串起来真的能用，不是各自单测。
        var payload = Encoding.UTF8.GetBytes("salvaged content");
        var zip = BuildZip("wiped.zip", ("keep.txt", payload));

        var map = ZipScanner.Inspect(zip);
        ClearRegion(zip, map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);

        var result = ZipRepairer.Repair(zip, ZipRepairMode.SalvageAndRepack);

        Assert.Contains("keep.txt", result.RecoveredEntries);
        Assert.Equal(payload, ReadBack(result.OutputPath)["keep.txt"]);
    }

    // ------------------------------------------------------------ 输出命名

    [Fact]
    public void SuggestedOutputNeverCollidesWithTheInputOrAPreviousRepair()
    {
        var zip = BuildZip("named.zip", ("a.txt", "x"u8.ToArray()));

        var first = ZipRepairer.SuggestOutputPath(zip);
        File.WriteAllText(first, "taken");
        var second = ZipRepairer.SuggestOutputPath(zip);

        Assert.NotEqual(zip, first);
        Assert.NotEqual(first, second);

        // 唯一化会插在扩展名之前：named.修复.zip → named.修复 (2).zip。
        Assert.Contains(".修复", second, StringComparison.Ordinal);
        Assert.EndsWith(".zip", second, StringComparison.OrdinalIgnoreCase);
    }
}
