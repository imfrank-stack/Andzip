using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 加密包在"没给口令"那一刻报的是什么。这条决定了界面是弹「输入密码」还是弹一句
/// 「该格式不支持」—— 用户报"记住口令没生效"，先得确认它到底有没有问过口令。
/// </summary>
public class EncryptedOpenFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-enc-").FullName;

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

    private string Create(string stem, bool encryptHeaders, string format = "7Z")
    {
        var source = Path.Combine(_root, stem + "-src");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "note.bin"), new byte[200_000]);

        var archive = Path.Combine(_root, stem + "." + format.ToLowerInvariant());
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            new ArchiveCreationSettings(
                archive,
                CompressionLevelOption.Store,
                Format: ArchiveFormatOption.FromId(format),
                Password: "hunter2",
                EncryptHeaders: encryptHeaders));
        return archive;
    }

    /// <summary>
    /// 头加密：不给口令时清单读不出来，报的必须是"要口令"。
    /// 引擎原生报的是 BAD_FORMAT（7-Zip 的处理器只回一句"这不是我的格式"），
    /// 界面据此会说「该格式不支持」—— 0.11.2 修的就是这一格。
    /// </summary>
    [Fact]
    public void ReadingHeaderEncryptedArchiveWithoutAPasswordReportsThatAPasswordIsNeeded()
    {
        var archive = Create("headers", encryptHeaders: true);

        var caught = Record.Exception(() => ArchiveReader.Read(archive, password: null));

        var failure = Assert.IsType<ArchiveFailureException>(caught);
        Assert.True(failure.IsPassword, $"报的是 {failure.Reason}（{failure.Message}），界面据此不会弹口令框。");
    }

    /// <summary>
    /// 只加密数据、头是明文：清单本来就读得出来，不该弹口令框 ——
    /// 但"这个包需要口令"仍然必须被报成 true，否则解压时会等到失败才知道要输。
    /// </summary>
    [Fact]
    public void DataOnlyEncryptionStillListsContentsButIsReportedAsNeedingAPassword()
    {
        var archive = Create("data", encryptHeaders: false);

        var listing = ArchiveReader.Read(archive, password: null);

        Assert.NotEmpty(listing.Entries);
        Assert.All(listing.Entries, entry => Assert.True(entry.IsEncrypted, $"{entry.Name} 没被标成加密"));
        Assert.True(ArchiveExtractor.IsPasswordProtected(archive));
    }

    [Fact]
    public void HeaderEncryptedArchiveIsReportedAsNeedingAPassword()
    {
        var headers = Create("probe", encryptHeaders: true);

        Assert.True(ArchiveExtractor.IsPasswordProtected(headers));
    }

    [Fact]
    public void PlainArchiveIsNotReportedAsNeedingAPassword()
    {
        var source = Path.Combine(_root, "plain-src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "plain");

        var archive = Path.Combine(_root, "plain.7z");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("7Z")));

        // 能正常打开的包不走探测：这条断言钉的是"没加密就别问口令"。
        Assert.False(ArchiveExtractor.IsPasswordProtected(archive));
    }

    [Fact]
    public void ProbeOnlySpeaksForFilesThatCarryTheSevenZipSignature()
    {
        var notAnArchive = Path.Combine(_root, "notes.txt");
        File.WriteAllText(notAnArchive, "just a file");

        Assert.False(EncryptedHeaderProbe.HasSevenZipSignature(notAnArchive));
        Assert.False(EncryptedHeaderProbe.LooksLikeMissingPassword(new InvalidDataException("x"), notAnArchive));
        Assert.False(EncryptedHeaderProbe.HasSevenZipSignature(Path.Combine(_root, "missing.7z")));
        Assert.False(EncryptedHeaderProbe.HasSevenZipSignature("   "));

        // 探测只认"签名 + 打不开"这两件事，分不出"缺口令"和"这个 7z 真坏了"——
        // 代价写在这里：坏包会多弹一次口令框，输完仍然报原来的错。
        var plain = Path.Combine(_root, "signed-but-broken.7z");
        File.WriteAllBytes(plain, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, .. new byte[64]]);
        Assert.True(EncryptedHeaderProbe.LooksLikeMissingPassword(new InvalidDataException("x"), plain));
        Assert.False(EncryptedHeaderProbe.LooksLikeMissingPassword(new FileNotFoundException(), plain));
    }
}
