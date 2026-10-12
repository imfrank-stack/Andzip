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

    [Fact]
    public void TestReportsMissingArchiveWithoutTouchingTheEngine()
    {
        Assert.Throws<FileNotFoundException>(() => ArchiveTester.Test(PathOf("nope.zip")));
    }
}
