using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

public class ArchiveCreatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-multi-").FullName;

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

    // ---------- helpers ----------

    private string PathOf(params string[] parts) => Path.Combine([_root, .. parts]);

    private string CreateFile(string relativePath, string content)
    {
        var full = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static Dictionary<string, string> ReadTar(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new TarReader(stream, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType == TarEntryType.Directory)
            {
                result[entry.Name] = "<dir>";
                continue;
            }

            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            result[entry.Name] = Encoding.UTF8.GetString(buffer.ToArray());
        }

        return result;
    }

    private static Dictionary<string, string> ReadTarFile(string path)
    {
        using var file = File.OpenRead(path);
        return ReadTar(file);
    }

    private static Dictionary<string, string> ReadTgzFile(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return ReadTar(gzip);
    }

    private static ArchiveCreationSettings Settings(
        string output,
        string formatId,
        CompressionLevelOption? level = null,
        bool test = false,
        bool deleteSource = false,
        bool separate = false)
        => new(
            output,
            level ?? CompressionLevelOption.Default,
            Format: ArchiveFormatOption.FromId(formatId),
            TestAfterCreation: test,
            DeleteSourceAfterCreation: deleteSource,
            SeparateArchives: separate);

    private static bool HasGzipMagic(string path)
    {
        using var file = File.OpenRead(path);
        return file.ReadByte() == 0x1F && file.ReadByte() == 0x8B;
    }

    // ---------- TAR / TGZ ----------

    [Fact]
    public void Tar_CreatesFolderArchiveWithRoundtrip()
    {
        CreateFile("src/a.txt", "A");
        CreateFile("src/sub/b.txt", "B");
        var output = PathOf("out.tar");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(output, "TAR") with { IncludeRootFolder = true });

        var entries = ReadTarFile(output);
        Assert.Equal("A", entries["src/a.txt"]);
        Assert.Equal("B", entries["src/sub/b.txt"]);
        Assert.Equal(2, result.FileCount);
        Assert.True(result.OutputBytes > 0);
    }

    [Fact]
    public void Tar_SingleFolder_DefaultsToNoRootFolder()
    {
        CreateFile("src/a.txt", "A");
        var output = PathOf("out.tar");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output, "TAR"));

        var entries = ReadTarFile(output);
        Assert.Equal("A", Assert.Contains("a.txt", entries));
        Assert.DoesNotContain("src/a.txt", entries.Keys);
    }

    [Fact]
    public void Tar_IncludesEmptyDirectories()
    {
        Directory.CreateDirectory(PathOf("src", "empty"));
        CreateFile("src/a.txt", "A");
        var output = PathOf("out.tar");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output, "TAR"));

        var entries = ReadTarFile(output);
        Assert.Contains(entries.Keys, k => k.TrimEnd('/') == "empty");
    }

    [Fact]
    public void Tgz_CreatesGzipCompressedTarWithRoundtrip()
    {
        CreateFile("src/a.txt", "HELLO-TGZ");
        var output = PathOf("out.tgz");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output, "TGZ"));

        Assert.True(HasGzipMagic(output));
        var entries = ReadTgzFile(output);
        Assert.Equal("HELLO-TGZ", Assert.Contains("a.txt", entries));
    }

    /// <summary>
    /// TGZ 的“0-仅存储”不会让产物变大：gzip 层没有存储模式（7-Zip 的 gzip 编码器各
    /// 级别都走 Deflate），tar 层本来就不压缩。所以这里钉住的是“级别被接受且不改变
    /// 正确性”，而不是一个不成立的大小关系。ZIP/7Z 上的大小差异另有测试覆盖。
    /// </summary>
    [Fact]
    public void Tgz_StoreLevel_IsAcceptedAndProducesValidArchive()
    {
        CreateFile("src/big.txt", new string('A', 200_000));
        var storeOutput = PathOf("store.tgz");
        var normalOutput = PathOf("normal.tgz");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(storeOutput, "TGZ", CompressionLevelOption.Store));
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(normalOutput, "TGZ", CompressionLevelOption.Normal));

        // 内层 tar 被完整还原，说明外层 gzip 的级别没有破坏结构。
        var listing = ArchiveReader.Read(storeOutput);
        Assert.Equal(200_000, Assert.Single(listing.Entries, e => !e.IsDirectory).Size);
        ArchiveTester.Test(storeOutput);
        ArchiveTester.Test(normalOutput);
    }

    // ---------- GZ ----------

    [Fact]
    public void Gz_SingleFile_Roundtrips()
    {
        var file = CreateFile("note.txt", "GZ-CONTENT");
        var output = PathOf("note.txt.gz");

        var result = ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, "GZ"));

        Assert.True(HasGzipMagic(output));
        using var input = File.OpenRead(output);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        Assert.Equal("GZ-CONTENT", reader.ReadToEnd());
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void Gz_Folder_FailsWithConstraint()
    {
        Directory.CreateDirectory(PathOf("src"));

        var ex = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(PathOf("out.gz"), "GZ")));

        Assert.Equal(ArchiveConstraintReason.GzRequiresSingleFile, ex.Reason);
    }

    [Fact]
    public void Gz_MultipleFiles_FailWithConstraint()
    {
        var a = CreateFile("a.txt", "A");
        var b = CreateFile("b.txt", "B");

        var ex = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveCreator.Create(
                [ArchiveInputItem.FromPath(a), ArchiveInputItem.FromPath(b)],
                Settings(PathOf("out.gz"), "GZ")));

        Assert.Equal(ArchiveConstraintReason.GzRequiresSingleFile, ex.Reason);
    }

    // ---------- 门面：不支持格式 ----------

    [Fact]
    public void Facade_UnsupportedFormat_ThrowsConstraint()
    {
        var file = CreateFile("a.txt", "A");

        var ex = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(PathOf("out.iso"), "ISO")));

        Assert.Equal(ArchiveConstraintReason.FormatNotSupported, ex.Reason);
    }

    // ---------- 压缩后测试 ----------

    [Fact]
    public void Facade_TestAfterCreation_PassesForFreshZip()
    {
        var file = CreateFile("a.txt", "A");
        var output = PathOf("tested.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, "ZIP", test: true));

        Assert.True(File.Exists(output));
    }

    [Fact]
    public void ArchiveTester_PassesForValidArchives()
    {
        var file = CreateFile("a.txt", "A");

        var zip = PathOf("t.zip");
        var tar = PathOf("t.tar");
        var tgz = PathOf("t.tgz");
        var gz = PathOf("a.txt.gz");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(zip, "ZIP"));
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(tar, "TAR"));
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(tgz, "TGZ"));
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(gz, "GZ"));

        ArchiveTester.Test(zip);
        ArchiveTester.Test(tar);
        ArchiveTester.Test(tgz);
        ArchiveTester.Test(gz);
    }

    [Fact]
    public void ArchiveTester_DetectsTruncatedZip()
    {
        // 用不可压缩的内容，保证压缩数据占据文件的大部分；否则改到的字节可能落在
        // 中央目录或填充区，CRC 依然对得上。
        var bytes = new byte[200_000];
        new Random(11).NextBytes(bytes);
        var file = PathOf("random.bin");
        File.WriteAllBytes(file, bytes);
        var zip = PathOf("broken.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(zip, "ZIP"));

        // 破坏压缩数据本身（而不是尾部目录）：CRC 一定对不上。
        using (var stream = new FileStream(zip, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(stream.Length / 4, SeekOrigin.Begin);
            stream.Write([0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8]);
        }

        Assert.ThrowsAny<Exception>(() => ArchiveTester.Test(zip));
    }

    /// <summary>
    /// 尾部（含中央目录）被截断时 7-Zip 会扫描本地文件头重建清单，因此可能“仍然打开成功”。
    /// 这是引擎的容错行为，不是 Core 的缺陷；钉在这里以免将来被当成回归。
    /// </summary>
    [Fact]
    public void ArchiveTester_ToleratesTruncatedEndOfCentralDirectory()
    {
        var file = CreateFile("a.txt", new string('A', 5000));
        var zip = PathOf("truncated.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(zip, "ZIP"));

        using (var stream = new FileStream(zip, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length / 2);
        }

        // 不抛异常就算通过；真正要保证的是“损坏的数据会被发现”，见上一个测试。
        ArchiveTester.Test(zip);
    }

    // ---------- 压缩后删除源文件 ----------

    [Fact]
    public void Facade_DeleteSource_RemovesSingleFileInput()
    {
        var file = CreateFile("gone.txt", "A");
        var output = PathOf("out.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, "ZIP", deleteSource: true));

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void SourceCleaner_DeletesDirectoryRecursively()
    {
        CreateFile("src/a.txt", "A");
        var item = ArchiveInputItem.FromPath(PathOf("src"));

        SourceCleaner.Delete([item], []);

        Assert.False(Directory.Exists(item.FullPath));
    }

    [Fact]
    public void SourceCleaner_SkipsInputThatContainsProtectedOutput()
    {
        CreateFile("src/a.txt", "A");
        var outputInside = PathOf("src", "out.zip");
        var item = ArchiveInputItem.FromPath(PathOf("src"));

        SourceCleaner.Delete([item], [outputInside]);

        Assert.True(Directory.Exists(item.FullPath), "产物位于输入目录内时必须跳过删除");
    }

    // ---------- 独立压缩 ----------

    [Fact]
    public void Facade_SeparateArchives_CreatesOneArchivePerInput()
    {
        var a = CreateFile("one.txt", "1");
        var b = CreateFile("two.txt", "2");
        var output = PathOf("ignored-name.zip");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(a), ArchiveInputItem.FromPath(b)],
            Settings(output, "ZIP", separate: true));

        Assert.Equal(2, result.CreatedArchives.Count);
        Assert.Contains(PathOf("one.zip"), result.CreatedArchives);
        Assert.Contains(PathOf("two.zip"), result.CreatedArchives);
        Assert.True(File.Exists(PathOf("one.zip")));
        Assert.True(File.Exists(PathOf("two.zip")));
        Assert.False(File.Exists(output), "独立压缩时不生成用户指定的合并产物");

        using var archive = ZipFile.OpenRead(PathOf("one.zip"));
        Assert.Single(archive.Entries);
    }

    [Fact]
    public void Facade_SeparateArchives_WithDelete_RemovesAllSourcesAndTestsEach()
    {
        var a = CreateFile("x.txt", "X");
        var b = CreateFile("y.txt", "Y");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(a), ArchiveInputItem.FromPath(b)],
            Settings(PathOf("ignored.zip"), "ZIP", test: true, deleteSource: true, separate: true));

        Assert.Equal(2, result.CreatedArchives.Count);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        foreach (var created in result.CreatedArchives)
        {
            Assert.True(File.Exists(created));
        }
    }

    [Fact]
    public void Facade_SeparateArchives_WithFolderInput_UsesFolderName()
    {
        CreateFile("docs/a.txt", "A");
        var output = PathOf("ignored.zip");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("docs"))],
            Settings(output, "ZIP", separate: true));

        Assert.Equal([PathOf("docs.zip")], result.CreatedArchives);
        using var archive = ZipFile.OpenRead(PathOf("docs.zip"));
        Assert.Equal("a.txt", archive.Entries.Single().FullName);
    }
}