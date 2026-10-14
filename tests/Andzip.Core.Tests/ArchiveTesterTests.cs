using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 完整性检测（FR-3.6）的对外契约：好包安静通过，坏包必须说出是哪个成员、坏在哪。
/// UI 那一侧只是把这两个结果显示出来，值得钉住的都在这里。
/// </summary>
public class ArchiveTesterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-test-").FullName;

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

    private string CreateNoiseFile(string relativePath, int size)
    {
        var full = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var noise = new byte[size];
        Random.Shared.NextBytes(noise);
        File.WriteAllBytes(full, noise);
        return full;
    }

    /// <summary>
    /// 语料必须"压不动"，一律用随机数据。第一版探针用的是 4000 个 <c>a</c>：去重之后整个包只剩
    /// 几百字节，"正中间那个字节"落在中央目录而不是数据区，翻坏它测试照样通过 —— 于是把一个坏包
    /// 判成了好的。要测 CRC 真的被校验，就得让被翻的那个字节确实在数据里。
    /// </summary>
    private string BuildNoiseZip(params string[] names)
    {
        var inputs = names.Select(n => ArchiveInputItem.FromPath(CreateNoiseFile(n, 200_000))).ToList();
        var output = PathOf(names.Length > 1 ? "multi.zip" : "solid.zip");
        ArchiveCreator.Create(inputs, new ArchiveCreationSettings(output, CompressionLevelOption.Max));
        return output;
    }

    /// <summary>把压缩数据里的一个字节翻掉：CRC 与内容都对不上，而目录结构还是好的。</summary>
    private static void CorruptAt(string archive, double fraction)
    {
        var bytes = File.ReadAllBytes(archive);
        bytes[(int)(bytes.Length * fraction)] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
    }

    [Fact]
    public void TestPassesOnAHealthyArchive()
    {
        var archive = BuildNoiseZip("a.bin");

        ArchiveTester.Test(archive);
    }

    [Fact]
    public void TestFailsAndNamesTheBrokenMember()
    {
        var archive = BuildNoiseZip("noise.bin");
        CorruptAt(archive, 0.5);

        var error = Assert.Throws<InvalidDataException>(() => ArchiveTester.Test(archive));

        // 「哪个包坏了」不够，界面得能回答「坏在哪一个文件」。
        Assert.Contains("noise.bin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestChecksEveryMemberNotJustTheFirst()
    {
        // 只校验第一个成员的实现看起来一模一样，要等到第二个坏掉才露馅 —— 所以坏的要放在后面。
        var archive = BuildNoiseZip("first.bin", "second.bin", "third.bin");
        CorruptAt(archive, 0.8);

        var error = Assert.Throws<InvalidDataException>(() => ArchiveTester.Test(archive));

        Assert.Contains("third.bin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestFailsOnATruncatedArchive()
    {
        // 真实世界里的坏包多半是"下载断在半路"，不是 CRC 打错一个字节 —— 截断必须也被判为坏。
        var archive = BuildNoiseZip("noise.bin");
        var bytes = File.ReadAllBytes(archive);
        using (var cut = File.OpenWrite(archive))
        {
            cut.SetLength(bytes.Length * 3 / 5);
        }

        Assert.Throws<InvalidDataException>(() => ArchiveTester.Test(archive));
    }

    /// <summary>
    /// 精确翻掉<strong>指名那个</strong>成员数据区里的一个字节。
    /// <para>按整包比例（<see cref="CorruptAt"/>）翻是"坏一个"的够用写法，但要测"坏的两个都被列出来"，
    /// 就得知道每个成员的数据从哪开始 —— 否则测试自己都不确定坏了谁，断言只能写成"大概"。</para>
    /// <para>认成员靠<strong>名字</strong>而不是"第几个 <c>PK\x03\x04</c>"：随机数据里恰好撞出这四个
    /// 字节的概率不为零，靠数签名会偶发翻错地方，那种红查起来要命。</para>
    /// <para>ZIP 局部头固定 30 字节，名字长度在 +26、扩展字段长度在 +28，数据紧跟在两者之后。</para>
    /// </summary>
    private static void CorruptMemberData(string archive, string entryName)
    {
        var bytes = File.ReadAllBytes(archive);
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        var at = -1;

        while ((at = IndexOf(bytes, [0x50, 0x4B, 0x03, 0x04], at + 1)) >= 0)
        {
            var nameLength = BitConverter.ToUInt16(bytes, at + 26);
            var extraLength = BitConverter.ToUInt16(bytes, at + 28);

            if (nameLength != name.Length
                || !bytes.AsSpan(at + 30, nameLength).SequenceEqual(name))
            {
                continue;
            }

            bytes[at + 30 + nameLength + extraLength + 4] ^= 0xFF;
            File.WriteAllBytes(archive, bytes);
            return;
        }

        throw new InvalidOperationException($"包里找不到成员「{entryName}」的局部头。");
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = Math.Max(0, from); i <= haystack.Length - needle.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void TestAllPassesQuietlyOnAHealthyArchive()
    {
        var archive = BuildNoiseZip("a.bin", "b.bin", "c.bin");

        var report = ArchiveTester.TestAll(archive);

        Assert.True(report.Passed);
        Assert.Equal(3, report.Members);
        Assert.Equal(3, report.Verified);
        Assert.Empty(report.Failures);
        Assert.Equal(0, report.Unexamined);
    }

    [Fact]
    public void TestAllListsEveryBrokenMember()
    {
        // 这一条就是 TestAll 存在的理由：引擎在第一个坏成员上就返回，而用户要的是整份名单。
        // 第一与第三都翻掉，只报一个的写法会漏 —— 所以两个名字都得在。
        var archive = BuildNoiseZip("first.bin", "second.bin", "third.bin");
        CorruptMemberData(archive, "first.bin");
        CorruptMemberData(archive, "third.bin");

        var report = ArchiveTester.TestAll(archive);

        Assert.False(report.Passed);
        Assert.Equal(["first.bin", "third.bin"], report.Failures.Select(f => f.Name).ToArray());
        // 中间那个真的查过并通过 —— "坏 2 个"与"没查 3 个"是两件不同的事。
        Assert.Equal(3, report.Members);
        Assert.Equal(1, report.Verified);
        Assert.All(report.Failures, failure => Assert.Contains("'", failure.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void TestAllNamesTheBrokenMemberEvenWhenTheNameCarriesAnApostrophe()
    {
        // 成员名是从内核的英文原文里截出来的（"CRC mismatch in '…'"）。按"最后一对引号"取，
        // 碰到 it's.txt 就会从名字中间切开 —— 所以取第一个引号到最后一个引号，这条盯着它。
        var archive = BuildNoiseZip("plain.bin", "it's.bin");
        CorruptMemberData(archive, "it's.bin");

        var report = ArchiveTester.TestAll(archive);

        Assert.Equal(["it's.bin"], report.Failures.Select(f => f.Name).ToArray());
        Assert.Equal(1, report.Verified);
    }

    [Fact]
    public void TestAllStillThrowsWhenTheArchiveCannotBeReadAtAll()
    {
        // "根本没读成"不是坏成员清单：格式不认识要照旧抛出去，别混成一份"0 个坏成员"的空报告。
        var junk = PathOf("not-an-archive.zip");
        File.WriteAllText(junk, "这不是包，一个字节的有效结构都没有。");

        Assert.ThrowsAny<Exception>(() => ArchiveTester.TestAll(junk));
    }

    [Fact]
    public void TestReportsMissingArchiveWithoutTouchingTheEngine()
    {
        Assert.Throws<FileNotFoundException>(() => ArchiveTester.Test(PathOf("nope.zip")));
        Assert.Throws<FileNotFoundException>(() => ArchiveTester.TestAll(PathOf("nope.zip")));
    }
}
