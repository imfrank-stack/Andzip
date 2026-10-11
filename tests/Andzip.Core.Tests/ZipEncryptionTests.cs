using System.IO.Compression;
using Andzip.Core.Archive;
using SharpStringCodec = ICSharpCode.SharpZipLib.Zip.StringCodec;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Andzip.Core.Tests;

/// <summary>ZIP 密码加密（FR-2.6）：AES-256 往返、错误密码拒绝、无密码路径不受影响。</summary>
public class ZipEncryptionTests : IDisposable
{
    // ZIP 的写入端（7-Zip）要求密码是纯 ASCII，所以这里的测试口令用 ASCII；
    // 非 ASCII 口令的行为由下面两个专门的测试固定下来。
    private const string Password = "P@ssw0rd-Enc";

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-zipcrypt-").FullName;

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
        string? password,
        CompressionLevelOption? level = null,
        EncryptionMethodOption? encryption = null)
        => new(output, level ?? CompressionLevelOption.Default, Password: password, Encryption: encryption);

    /// <summary>用 SharpZipLib 读取条目（密码为 null 表示不提供密码）。</summary>
    private static Dictionary<string, string> ReadEntries(string zipPath, string? password)
    {
        using var zip = new SharpZipFile(zipPath, SharpStringCodec.FromCodePage(65001));
        if (password is not null)
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

    // ---------- 加密创建 ----------

    [Fact]
    public void Create_WithPassword_DefaultEncryptionIsZipCrypto()
    {
        var file = CreateFile("note.txt", "secret");
        var output = PathOf("enc.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password));

        using var zip = new SharpZipFile(output, SharpStringCodec.FromCodePage(65001));
        var entry = zip[0];
        Assert.NotNull(entry);
        Assert.True(entry!.IsCrypted, "设置密码后条目应为加密状态");
        Assert.Equal(0, entry.AESKeySize);
    }

    [Fact]
    public void Create_WithPassword_ExplicitAes256_UsesAesEntries()
    {
        var file = CreateFile("note.txt", "secret");
        var output = PathOf("enc-aes.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, Password, encryption: EncryptionMethodOption.Aes256));

        using var zip = new SharpZipFile(output, SharpStringCodec.FromCodePage(65001));
        var entry = zip[0];
        Assert.NotNull(entry);
        Assert.True(entry!.IsCrypted);
        Assert.Equal(256, entry.AESKeySize);
    }

    [Fact]
    public void Create_WithPassword_RoundTripsWithCorrectPassword()
    {
        CreateFile("src/a.txt", "A-内容");
        CreateFile("src/sub/b.txt", "B");
        Directory.CreateDirectory(PathOf("src", "empty"));
        var output = PathOf("enc.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output, Password));

        var entries = ReadEntries(output, Password);
        Assert.Equal("A-内容", entries["a.txt"]);
        Assert.Equal("B", entries["sub/b.txt"]);
        Assert.Equal("<dir>", entries["empty/"]);
    }

    [Fact]
    public void Create_WithPassword_CannotBeReadWithoutPassword()
    {
        var file = CreateFile("note.txt", "secret");
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password));

        Assert.ThrowsAny<Exception>(() => ReadEntries(output, password: null));
    }

    [Fact]
    public void Create_WithPassword_WrongPasswordFails()
    {
        var file = CreateFile("note.txt", "secret");
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password));

        Assert.ThrowsAny<Exception>(() => ReadEntries(output, "wrong-password"));
    }

    [Fact]
    public void Create_WithPassword_StandardReaderCannotRecoverPlaintext()
    {
        var file = CreateFile("note.txt", "secret");
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password));

        string? content = null;
        try
        {
            using var archive = ZipFile.OpenRead(output);
            using var reader = new StreamReader(archive.Entries.Single().Open());
            content = reader.ReadToEnd();
        }
        catch (Exception)
        {
            // 标准读取器通常直接抛异常，这也是可接受的结果
        }

        Assert.NotEqual("secret", content);
    }

    [Fact]
    public void Create_WithEmptyPassword_IsNotEncrypted()
    {
        var file = CreateFile("note.txt", "plain");
        var output = PathOf("plain.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, password: string.Empty));

        using (var archive = ZipFile.OpenRead(output))
        {
            using var reader = new StreamReader(archive.Entries.Single().Open());
            Assert.Equal("plain", reader.ReadToEnd());
        }

        using var zip = new SharpZipFile(output);
        Assert.False(zip[0]!.IsCrypted);
    }

    [Fact]
    public void Create_Encrypted_StoreLevel_LargerThanMaximum()
    {
        var file = CreateFile("big.txt", new string('A', 200_000));
        var store = PathOf("enc-store.zip");
        var max = PathOf("enc-max.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(store, Password, CompressionLevelOption.Store));
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(max, Password, CompressionLevelOption.Max));

        Assert.True(
            new FileInfo(store).Length > new FileInfo(max).Length,
            "加密路径下 0-仅存储 的产物应明显大于 3-极限压缩");
    }

    [Fact]
    public void Create_Encrypted_Cancelled_RemovesPartialOutput()
    {
        var file = CreateFile("a.txt", "A");
        var output = PathOf("enc.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password), cts.Token));

        Assert.False(File.Exists(output), "取消后不应留下半成品产物");
    }

    // ---------- 完整性测试（带密码） ----------

    [Fact]
    public void Tester_EncryptedArchive_PassesWithCorrectPassword()
    {
        CreateFile("src/a.txt", new string('B', 5000));
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], Settings(output, Password));

        ArchiveTester.Test(output, Password);
    }

    [Fact]
    public void Tester_EncryptedArchive_ThrowsWithWrongPassword()
    {
        var file = CreateFile("a.txt", "A");
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password));

        Assert.ThrowsAny<Exception>(() => ArchiveTester.Test(output, "wrong-password"));
    }

    [Fact]
    public void Tester_EncryptedArchive_ThrowsWithoutPassword()
    {
        var file = CreateFile("a.txt", "A");
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, Password));

        Assert.ThrowsAny<Exception>(() => ArchiveTester.Test(output));
    }

    // ---------- 门面组合 ----------

    [Fact]
    public void Facade_TestAfterCreation_WithPassword_Passes()
    {
        var file = CreateFile("a.txt", new string('A', 10_000));
        var output = PathOf("tested-enc.zip");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, Password) with { TestAfterCreation = true });

        Assert.Equal(output, Assert.Single(result.CreatedArchives));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Facade_SeparateArchives_WithPassword_EncryptsEach()
    {
        var a = CreateFile("one.txt", "1");
        var b = CreateFile("two.txt", "2");

        var result = ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(a), ArchiveInputItem.FromPath(b)],
            Settings(PathOf("ignored.zip"), Password) with { SeparateArchives = true, TestAfterCreation = true });

        Assert.Equal(2, result.CreatedArchives.Count);
        foreach (var created in result.CreatedArchives)
        {
            var entries = ReadEntries(created, Password);
            Assert.Single(entries);
        }
    }

    /// <summary>
    /// ZIP 的密码只接受 ASCII：7-Zip 的 zip 写入端在拿到非 ASCII 密码时直接返回
    /// E_INVALIDARG，Core 提前把它翻译成一个可本地化的约束异常，而不是让 Win32 87 冒出来。
    /// </summary>
    [Fact]
    public void Create_Zip_NonAsciiPassword_IsRejectedWithConstraint()
    {
        var file = CreateFile("note.txt", "secret");

        var ex = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveCreator.Create(
                [ArchiveInputItem.FromPath(file)],
                Settings(PathOf("ascii.zip"), "密码-非ASCII")));

        Assert.Equal(ArchiveConstraintReason.NonAsciiPasswordNotSupported, ex.Reason);
    }

    /// <summary>7z 用 UTF-16 口令派生密钥，中文密码可以正常往返 —— 这是给用户的出路。</summary>
    [Fact]
    public void Create_SevenZip_NonAsciiPassword_RoundTrips()
    {
        var file = CreateFile("cn.txt", "中文内容");
        var output = PathOf("cn.7z");
        const string password = "P@ssw0rd-加密";

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            new ArchiveCreationSettings(
                output, CompressionLevelOption.Normal, Format: ArchiveFormatOption.FromId("7Z"), Password: password));

        var listing = ArchiveReader.Read(output, password);
        Assert.Equal("cn.txt", Assert.Single(listing.Entries, e => !e.IsDirectory).Name);
        ArchiveTester.Test(output, password);
    }
}