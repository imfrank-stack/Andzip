using System.IO.Compression;
using Andzip.Core.Archive;
using SharpStringCodec = ICSharpCode.SharpZipLib.Zip.StringCodec;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Andzip.Core.Tests;

/// <summary>
/// 高级设置（FR-2.5）：压缩方法（Store / Deflate / BZip2 / LZMA）、加密算法（ZipCrypto 默认 / AES-256）、
/// 条目「文件说明」（注释）。
/// </summary>
public class ZipAdvancedSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-adv-").FullName;

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

    private static ArchiveCreationSettings Settings(
        string output,
        CompressionMethodOption? method = null,
        EncryptionMethodOption? encryption = null,
        string? password = null,
        string? comment = null)
        => new(
            output,
            CompressionLevelOption.Default,
            Method: method,
            Encryption: encryption,
            Password: password,
            EntryComment: comment);

    private static Dictionary<string, string> ReadEntries(string zipPath, string? password = null)
    {
        using var zip = new SharpZipFile(zipPath, SharpStringCodec.FromCodePage(65001));
        if (!string.IsNullOrEmpty(password))
        {
            zip.Password = password;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SharpZipEntry entry in zip)
        {
            if (!entry.IsFile)
            {
                result[entry.Name] = "<dir>";
                continue;
            }

            using var stream = zip.GetInputStream(entry);
            using var reader = new StreamReader(stream);
            result[entry.Name] = reader.ReadToEnd();
        }

        return result;
    }

    private static SharpZipEntry FirstFileEntry(string zipPath, string? password = null)
    {
        var zip = new SharpZipFile(zipPath, SharpStringCodec.FromCodePage(65001));
        if (!string.IsNullOrEmpty(password))
        {
            zip.Password = password;
        }

        foreach (SharpZipEntry entry in zip)
        {
            if (entry.IsFile)
            {
                return entry;
            }
        }

        throw new InvalidOperationException("No file entry found.");
    }

    // ---------- 选项表 ----------

    [Fact]
    public void MethodOptions_ArePerFormatAndKeepTheReferenceDefault()
    {
        // 方法表按格式给出，第一项 = 默认（对齐参考软件实测的 ZIP 默认 Deflate）。
        var zip = ArchiveFormatOption.FromId("ZIP").Methods.Select(m => m.EngineMethod).ToList();
        Assert.Equal("Deflate", zip[0]);
        Assert.Contains("Copy", zip);
        Assert.Contains("BZip2", zip);
        Assert.Contains("LZMA", zip);

        // 换引擎后新解锁的格式也有真实方法表，不再是 ZIP 专属。
        Assert.Equal("LZMA2", ArchiveFormatOption.FromId("7Z").Methods[0].EngineMethod);

        // 单流 / 纯打包格式没有可选项：给它们下发 -mm= 会被引擎拒绝。
        Assert.Empty(ArchiveFormatOption.FromId("GZ").Methods);
        Assert.Empty(ArchiveFormatOption.FromId("TAR").Methods);
        Assert.Empty(ArchiveFormatOption.FromId("XZ").Methods);

        // 不可创建的格式没有方法可选。
        Assert.Empty(ArchiveFormatOption.FromId("ISO").Methods);
    }

    /// <summary>非标准方法号 —— 只有 zstd/lz4 那一系 fork 的内核认得。</summary>
    private static readonly string[] NonStandardEverywhere = ["ZSTD", "LZ4", "LZ5", "LIZARD", "BROTLI"];

    [Fact]
    public void NonStandardMethodsAreNotOfferedAnywhere_Yet()
    {
        // 两条独立的理由，目前都还成立，所以任何一种都不许出现在下拉框里：
        //   1) ZIP：非标准方法号写出来的包 WinRAR / 资源管理器 / 原版 7-Zip 全都打不开，
        //      而用户会把这种包发给别人 —— 这是用户定的红线，与内核能力无关。
        //   2) 7z：内核登记了编码器，但 7z 的写入路径仍拒绝它们（-mm=LZ4 实测 E_INVALIDARG）。
        // 等 2) 在内核侧修通，7z 这一半要放开、ZIP 那一半永远不放开 —— 到那时改这条断言。
        var zip = ArchiveFormatOption.FromId("ZIP").Methods.Select(m => m.EngineMethod).ToList();
        var sevenZip = ArchiveFormatOption.FromId("7Z").Methods.Select(m => m.EngineMethod).ToList();

        foreach (var forbidden in NonStandardEverywhere)
        {
            Assert.DoesNotContain(forbidden, zip, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, sevenZip, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SevenZipOnlyListsMethodsThatRoundTrippedThroughTheEngine()
    {
        // 第一项就是默认方法，所以这张表的顺序是行为的一部分，不是随手写的列表。
        Assert.Equal(["LZMA2", "LZMA", "PPMd", "BZip2", "Deflate", "Copy"],
            ArchiveFormatOption.FromId("7Z").Methods.Select(m => m.EngineMethod).ToArray());
    }

    [Fact]
    public void EncryptionOptions_MatchReferenceDefaults()
    {
        Assert.Equal([EncryptionMethodOption.ZipCrypto, EncryptionMethodOption.Aes256], EncryptionMethodOption.All);
        Assert.Equal(EncryptionMethodOption.ZipCrypto, EncryptionMethodOption.Default);
        Assert.True(EncryptionMethodOption.Aes256.IsAes);
        Assert.False(EncryptionMethodOption.ZipCrypto.IsAes);
    }

    // ---------- 压缩方法 ----------

    [Fact]
    public void Create_DefaultMethod_IsDeflate()
    {
        // 数据必须真的可压：7-Zip 的 ZIP 写入端在 Deflate 反而变大时会退回 Store，
        // 这是引擎的正确行为（7z.exe 也一样），所以下面用可压缩内容。
        var file = CreateFile("a.txt", new string('A', 4000));
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output));

        Assert.Equal(8, (int)FirstFileEntry(output).CompressionMethod);
    }

    /// <summary>压不动的内容（随机字节）会被 7-Zip 自动存成 Store，而不是膨胀。</summary>
    [Fact]
    public void Create_IncompressibleData_FallsBackToStore()
    {
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        var file = PathOf("random.bin");
        File.WriteAllBytes(file, bytes);
        var output = PathOf("random.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output));

        Assert.Equal(0, (int)FirstFileEntry(output).CompressionMethod);
        Assert.True(new FileInfo(output).Length < bytes.Length + 4096);
    }

    [Fact]
    public void Create_StoreMethod_ProducesUncompressedEntries()
    {
        var file = CreateFile("big.txt", new string('A', 100_000));
        var output = PathOf("store.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, CompressionMethodOption.Store));

        var entry = FirstFileEntry(output);
        Assert.Equal(0, (int)entry.CompressionMethod);
        Assert.True(new FileInfo(output).Length >= 100_000, "仅存储方法下产物不应小于原始数据");
        ArchiveTester.Test(output);
    }

    [Theory]
    [InlineData("Copy", 0)] // ZIP 规范编号：Stored
    [InlineData("Deflate", 8)]
    public void Create_EachCompressionMethod_RoundTrips(string engineMethod, int zipMethodCode)
    {
        var method = ArchiveFormatOption.FromId("ZIP").Methods.Single(m => m.EngineMethod == engineMethod);
        // 可压缩内容，否则 7-Zip 会自作聪明退回 Store（见 Create_DefaultMethod_IsDeflate）。
        var payload = "高级设置压缩方法-" + zipMethodCode + new string('A', 4000);
        var file = CreateFile("data.txt", payload);
        var output = PathOf($"m{zipMethodCode}.zip");

        var result = ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, method));

        // 用 SharpZipLib 这个独立实现读回来核对方法号，而不是自说自话。
        Assert.Equal(zipMethodCode, (int)FirstFileEntry(output).CompressionMethod);
        Assert.Equal(payload, Assert.Contains("data.txt", ReadEntries(output)));
        ArchiveTester.Test(output);
        Assert.Equal([Path.GetFullPath(output)], result.CreatedArchives);
    }

    [Fact]
    public void Create_StoreMethod_IsReadableByStandardReader()
    {
        var file = CreateFile("data.txt", "store-payload");
        var output = PathOf("store-plain.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, CompressionMethodOption.Store));

        using var archive = ZipFile.OpenRead(output);
        using var reader = new StreamReader(archive.Entries.Single().Open());
        Assert.Equal("store-payload", reader.ReadToEnd());
    }

    // ---------- 条目「文件说明」 ----------

    /// <summary>
    /// 条目「文件说明」需要逐条目注释，native 引擎没有把它接到 ABI 上（归档级注释 7-Zip
    /// 的写入端也不支持）。行为要求是：包照样写成功，但必须把“没做”这件事回报给调用方，
    /// 而不是静默丢弃用户输入。
    /// </summary>
    [Fact]
    public void Create_WithEntryComment_ReportsUnsupportedAndStillWrites()
    {
        CreateFile("src/a.txt", "A");
        Directory.CreateDirectory(PathOf("src", "empty"));
        var output = PathOf("comment.zip");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            Settings(output, comment: "文件说明文本"));

        Assert.Contains(result.Warnings!, warning => warning.Contains("entry comments", StringComparison.Ordinal));
        Assert.Empty(FirstFileEntry(output).Comment ?? string.Empty);
        ArchiveTester.Test(output);
    }

    [Fact]
    public void Create_WithoutEntryComment_CommentIsEmpty()
    {
        var file = CreateFile("a.txt", "A");
        var output = PathOf("nocomment.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output));

        Assert.True(string.IsNullOrEmpty(FirstFileEntry(output).Comment));
    }

    // ---------- 加密算法 ----------

    [Fact]
    public void Create_WithPassword_DefaultEncryptionIsZipCrypto()
    {
        var file = CreateFile("a.txt", "secret");
        var output = PathOf("zipcrypto.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, password: "Ascii-Pass-1"));

        var entry = FirstFileEntry(output);
        Assert.True(entry.IsCrypted);
        Assert.Equal(0, entry.AESKeySize);
        Assert.Equal("secret", Assert.Contains("a.txt", ReadEntries(output, "Ascii-Pass-1")));
        ArchiveTester.Test(output, "Ascii-Pass-1");
    }

    [Fact]
    public void Create_WithAes256Requested_UsesAesEntries()
    {
        var file = CreateFile("a.txt", "secret");
        var output = PathOf("aes.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, encryption: EncryptionMethodOption.Aes256, password: "Ascii-Pass-2"));

        var entry = FirstFileEntry(output);
        Assert.True(entry.IsCrypted);
        Assert.Equal(256, entry.AESKeySize);
        ArchiveTester.Test(output, "Ascii-Pass-2");
    }

    [Fact]
    public void Create_ZipCrypto_WrongPasswordFails()
    {
        var file = CreateFile("a.txt", "secret");
        var output = PathOf("zipcrypto.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, password: "right"));

        Assert.ThrowsAny<Exception>(() => ReadEntries(output, "wrong"));
        Assert.ThrowsAny<Exception>(() => ArchiveTester.Test(output, "wrong"));
    }

    [Fact]
    public void Create_ZipCrypto_StandardReaderCannotRecoverPlaintext()
    {
        var file = CreateFile("a.txt", "secret");
        var output = PathOf("zipcrypto.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, password: "right"));

        string? content = null;
        try
        {
            using var archive = ZipFile.OpenRead(output);
            using var reader = new StreamReader(archive.Entries.Single().Open());
            content = reader.ReadToEnd();
        }
        catch (Exception)
        {
        }

        Assert.NotEqual("secret", content);
    }

    // ---------- 组合场景 ----------

    [Fact]
    public void Create_StoreAesAndComment_CombinedRoundTrips()
    {
        var file = CreateFile("combo.txt", "组合场景内容");
        var output = PathOf("combo.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(
                output,
                method: CompressionMethodOption.Store,
                encryption: EncryptionMethodOption.Aes256,
                password: "Ascii-Pass-3",
                comment: "组合说明"));

        var entry = FirstFileEntry(output, "Ascii-Pass-3");
        Assert.Equal(0, (int)entry.CompressionMethod);
        Assert.Equal(256, entry.AESKeySize);
        // 条目注释不被引擎支持：包仍然要写对，并且要回报没做。
        Assert.Empty(entry.Comment ?? string.Empty);
        Assert.Equal("组合场景内容", Assert.Contains("combo.txt", ReadEntries(output, "Ascii-Pass-3")));
        ArchiveTester.Test(output, "Ascii-Pass-3");
    }

    [Fact]
    public void Facade_ExtendedSettings_TestAfterCreation_Passes()
    {
        var file = CreateFile("a.txt", new string('C', 5000));
        var output = PathOf("facade.zip");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(
                output,
                method: CompressionMethodOption.Store,
                encryption: EncryptionMethodOption.Aes256,
                password: "Ascii-Pass-5",
                comment: "门面组合") with { TestAfterCreation = true });

        Assert.Equal([Path.GetFullPath(output)], result.CreatedArchives);
        Assert.True(File.Exists(output));
    }
}