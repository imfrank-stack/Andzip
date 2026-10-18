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
        // ZIP 没有 LZMA2 这个方法（内核的 ZIP handler 只有 kLZMA，AE 字段只带 LZMA1）：
        // 列出来就是一次"读完整个文件才告诉你 E_NOTIMPL"，2026-10-18 实测过才拿掉的。
        Assert.DoesNotContain("LZMA2", zip, StringComparer.OrdinalIgnoreCase);

        // 换引擎后新解锁的格式也有真实方法表，不再是 ZIP 专属。
        Assert.Equal("LZMA2", ArchiveFormatOption.FromId("7Z").Methods[0].EngineMethod);

        // 单流 / 纯打包格式没有可选项：给它们下发 -mm= 会被引擎拒绝。
        Assert.Empty(ArchiveFormatOption.FromId("GZ").Methods);
        Assert.Empty(ArchiveFormatOption.FromId("TAR").Methods);
        Assert.Empty(ArchiveFormatOption.FromId("XZ").Methods);

        // 不可创建的格式没有方法可选。
        Assert.Empty(ArchiveFormatOption.FromId("ISO").Methods);
    }

    /// <summary>非标准方法号 —— 只有带这些编码器的 7-Zip 分支（我们的内核、7-Zip-zstd、NanaZip）认得。</summary>
    private static readonly string[] ExtensionMethods = ["ZSTD", "LZ4", "LZ5", "LIZARD", "BROTLI"];

    /// <summary>
    /// 那五个扩展方法：<strong>7z 一律给，ZIP 一个都不给</strong>。
    /// <para>2.2.0 之前这条断言是"两边都不给"，理由有两条，其中"7z 写不出来"那条已经消失 ——
    /// 五种方法 × 五档级别、外加带字典 / 带口令 / 非固实 / 多输入全部实测往返一致
    /// （常驻检查在 <c>Andzip.Native.Tests</c>）。留下来的这条是用户定的红线，与内核能力无关：
    /// 非标准方法号写进 ZIP，WinRAR / 资源管理器 / 原版 7-Zip 全都打不开，而用户会把这种包发给别人。
    /// 内核那边也会拒（实测 <c>-f zip -m LZ4</c> 回 E_NOTIMPL），所以这条不是只靠"界面没列"。</para>
    /// </summary>
    [Fact]
    public void ExtensionMethodsAreOfferedForSevenZipButNeverForZip()
    {
        var zip = ArchiveFormatOption.FromId("ZIP").Methods.Select(m => m.EngineMethod).ToList();
        var sevenZip = ArchiveFormatOption.FromId("7Z").Methods.Select(m => m.EngineMethod).ToList();

        foreach (var method in ExtensionMethods)
        {
            Assert.DoesNotContain(method, zip, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(method, sevenZip, StringComparer.OrdinalIgnoreCase);
        }

        // 其余格式（tar / gz / xz 那些）压根没有方法表，扩展方法也不该凭空冒出来
        Assert.DoesNotContain("LZ4", ArchiveFormatOption.FromId("ISO").Methods.Select(m => m.EngineMethod));
    }

    [Fact]
    public void SevenZipOnlyListsMethodsThatRoundTrippedThroughTheEngine()
    {
        // 第一项就是默认方法，所以这张表的顺序是行为的一部分，不是随手写的列表。
        // 标准方法在前（原版 7-Zip 也认），扩展方法居中，「仅存储」永远垫底。
        Assert.Equal(
            ["LZMA2", "LZMA", "PPMd", "BZip2", "Deflate", "ZSTD", "LZ4", "LZ5", "LIZARD", "BROTLI", "Copy"],
            ArchiveFormatOption.FromId("7Z").Methods.Select(m => m.EngineMethod).ToArray());
    }

    /// <summary>界面上那一句话只对扩展方法显示；判据是这张表，不是"名字看起来像专有名词"。</summary>
    [Theory]
    [InlineData("ZSTD", true)]
    [InlineData("LZ4", true)]
    [InlineData("LZ5", true)]
    [InlineData("LIZARD", true)]
    [InlineData("BROTLI", true)]
    [InlineData("LZMA2", false)]
    [InlineData("LZMA", false)]
    [InlineData("PPMd", false)]
    [InlineData("BZip2", false)]
    [InlineData("Deflate", false)]
    [InlineData("Copy", false)]
    public void TheExtensionFlagDrivesTheWarningLine(string engineMethod, bool expected)
    {
        var method = ArchiveFormatOption.FromId("7Z").Methods.Single(m => m.EngineMethod == engineMethod);

        Assert.Equal(expected, CompressionMethodOption.IsExtension(method));
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