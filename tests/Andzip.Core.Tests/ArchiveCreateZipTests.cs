using System.IO.Compression;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

public class ArchiveCreateZipTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-zip-").FullName;

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

    private static Dictionary<string, string> ReadEntries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                result[entry.FullName] = "<dir>";
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = reader.ReadToEnd();
        }

        return result;
    }

    private static ArchiveCreationSettings Settings(string output, CompressionLevelOption? level = null)
        => new(output, level ?? CompressionLevelOption.Default);

    // ---------- tests ----------

    [Fact]
    public void Create_SingleFile_EntryKeepsNameAndContent()
    {
        var file = CreateFile("note.txt", "hello");
        var output = PathOf("out.zip");

        var result = ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output));

        var entries = ReadEntries(output);
        Assert.Equal("hello", Assert.Contains("note.txt", entries));
        Assert.Equal(1, result.FileCount);
        Assert.Equal(0, result.DirectoryCount);
        Assert.Equal(5, result.TotalInputBytes);
        Assert.True(result.OutputBytes > 0);
    }

    [Fact]
    public void Create_Folder_IncludesNestedFilesUnderFolderRoot()
    {
        CreateFile("src/a.txt", "A");
        CreateFile("src/sub/b.txt", "B");
        var output = PathOf("out.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(output) with { IncludeRootFolder = true });

        var entries = ReadEntries(output);
        Assert.Equal("A", entries["src/a.txt"]);
        Assert.Equal("B", entries["src/sub/b.txt"]);
    }

    [Fact]
    public void Create_SingleFolder_DefaultsToNoRootFolder()
    {
        CreateFile("src/a.txt", "A");
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output));

        var entries = ReadEntries(output);
        Assert.Equal("A", Assert.Contains("a.txt", entries));
        Assert.DoesNotContain("src/a.txt", entries.Keys);
    }

    [Fact]
    public void Create_MultipleInputs_EachKeepsOwnRoot()
    {
        var file = CreateFile("one.txt", "1");
        CreateFile("src/two.txt", "2");
        var output = PathOf("out.zip");

        // 多个输入时即使都是单层，也各自保留自身名称（不触发“单文件夹去根”规则）
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file), ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(output));

        var entries = ReadEntries(output);
        Assert.Equal("1", entries["one.txt"]);
        Assert.Equal("2", entries["src/two.txt"]);
    }

    [Fact]
    public void Create_ExcludesThumbsDb_ByDefault()
    {
        CreateFile("src/keep.txt", "keep");
        CreateFile("src/Thumbs.db", "cache");
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output));

        var entries = ReadEntries(output);
        Assert.Contains("keep.txt", entries.Keys);
        Assert.DoesNotContain("Thumbs.db", entries.Keys);
    }

    [Fact]
    public void Create_IncludesThumbsDb_WhenDisabled()
    {
        CreateFile("src/Thumbs.db", "cache");
        var output = PathOf("out.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(output) with { ExcludeThumbsDb = false });

        var entries = ReadEntries(output);
        Assert.Contains("Thumbs.db", entries.Keys);
    }

    [Fact]
    public void Create_IncludesEmptyDirectories()
    {
        Directory.CreateDirectory(PathOf("src", "empty"));
        CreateFile("src/a.txt", "A");
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output));

        var entries = ReadEntries(output);
        Assert.Equal("<dir>", Assert.Contains("empty/", entries));
    }

    [Fact]
    public void Create_SkipsOutputFileInsideInputFolder()
    {
        CreateFile("src/a.txt", "A");
        var output = PathOf("src", "out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output));

        var entries = ReadEntries(output);
        Assert.DoesNotContain("out.zip", entries.Keys);
        Assert.Single(entries);
    }

    [Fact]
    public void Create_EmptyInputs_Throws()
    {
        Assert.Throws<EmptyArchiveInputException>(
            () => ArchiveCreator.Create([], Settings(PathOf("out.zip"))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Create_EachCompressionLevel_ProducesReadableArchive(int levelId)
    {
        var file = CreateFile("data.txt", new string('A', 4096));
        var output = PathOf($"out-{levelId}.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, CompressionLevelOption.FromId(levelId)));

        var entries = ReadEntries(output);
        Assert.Equal(4096, entries["data.txt"].Length);
    }

    [Fact]
    public void Create_StoreLevel_IsLargerThanNormalForCompressibleData()
    {
        var file = CreateFile("big.txt", new string('A', 200_000));
        var storeOutput = PathOf("store.zip");
        var normalOutput = PathOf("normal.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(storeOutput, CompressionLevelOption.Store));
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(normalOutput, CompressionLevelOption.Normal));

        var storeSize = new FileInfo(storeOutput).Length;
        var normalSize = new FileInfo(normalOutput).Length;
        Assert.True(
            storeSize > normalSize,
            $"0-仅存储 的产物应明显大于 2-正常压缩（实际 store={storeSize}, normal={normalSize}）");
    }

    [Fact]
    public void Create_Cancelled_ThrowsAndRemovesPartialOutput()
    {
        var file = CreateFile("a.txt", "A");
        var output = PathOf("out.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output), cts.Token));

        Assert.False(File.Exists(output), "取消后不应留下半成品产物");
    }

    [Fact]
    public void Create_Overwrite_ReplacesExistingOutput()
    {
        var file = CreateFile("a.txt", "NEW");
        var output = PathOf("out.zip");
        File.WriteAllText(output, "old junk");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output));

        var entries = ReadEntries(output);
        Assert.Equal("NEW", entries["a.txt"]);
    }

    [Fact]
    public void Create_UnicodeFileNames_ArePreserved()
    {
        var file = CreateFile("中文名字.txt", "内容");
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output));

        var entries = ReadEntries(output);
        Assert.Equal("内容", entries["中文名字.txt"]);
    }

    [Fact]
    public void Create_ReportsCountsAndSizes()
    {
        CreateFile("src/a.txt", "12345");
        CreateFile("src/sub/b.txt", "123");
        Directory.CreateDirectory(PathOf("src", "empty"));
        var output = PathOf("out.zip");

        var result = ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output));

        Assert.Equal(2, result.FileCount);
        Assert.Equal(2, result.DirectoryCount); // sub/ 与 empty/
        Assert.Equal(8, result.TotalInputBytes);
        Assert.Equal(Path.GetFullPath(output), result.OutputPath);
        Assert.True(result.OutputBytes > 0);
    }
}