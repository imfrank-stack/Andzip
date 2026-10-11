using System.IO.Compression;
using System.Text;
using Andzip.Core.Archive;
using SharpStringCodec = ICSharpCode.SharpZipLib.Zip.StringCodec;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipOutputStream = ICSharpCode.SharpZipLib.Zip.ZipOutputStream;

namespace Andzip.Core.Tests;

/// <summary>解压引擎（FR-3）：ZIP / TAR / TGZ / GZ、分卷、密码、智能解压、重名策略与安全防护。</summary>
public class ExtractionTests : IDisposable
{
    private const long OneKb = 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-extract-").FullName;

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

    private string NewDestination(string name = "dest")
    {
        var path = PathOf(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ArchiveCreationSettings CreateSettings(string output, string formatId = "ZIP", string? password = null, long? volumeSize = null)
        => new(
            output,
            CompressionLevelOption.Default,
            Format: ArchiveFormatOption.FromId(formatId),
            Password: password,
            VolumeSizeBytes: volumeSize);

    private static ExtractionSettings Extraction(string archive, string destination, bool smart = false, string? password = null)
        => new(archive, destination, OverwritePolicy.Overwrite, Password: password, StripSingleRootFolder: smart);

    private static string ReadText(string path) => File.ReadAllText(path);

    // ---------- ZIP 基本解压 ----------

    [Fact]
    public void Zip_Extract_ExtractsFilesDirectoriesAndCounts()
    {
        CreateFile("src/a.txt", "A");
        CreateFile("src/sub/b.txt", "BB");
        Directory.CreateDirectory(PathOf("src", "empty"));
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        var result = ArchiveExtractor.Extract(Extraction(zip, destination));

        Assert.Equal("A", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal("BB", ReadText(Path.Combine(destination, "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
        Assert.Equal(2, result.FileCount);
        Assert.Equal(2, result.DirectoryCount);
        Assert.Equal(3, result.TotalBytes);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(destination, result.DestinationPath);
    }

    [Fact]
    public void Zip_Extract_PreservesUnicodeNames()
    {
        CreateFile("src/中文名字.txt", "内容");
        var zip = PathOf("unicode.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination));

        Assert.Equal("内容", ReadText(Path.Combine(destination, "中文名字.txt")));
    }

    [Fact]
    public void Zip_Extract_Encrypted_RoundTripsWithPassword()
    {
        CreateFile("src/secret.txt", "secret-content");
        var zip = PathOf("enc.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(zip, password: "pw-123") with { Encryption = EncryptionMethodOption.Aes256 });

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination, password: "pw-123"));

        Assert.Equal("secret-content", ReadText(Path.Combine(destination, "secret.txt")));
    }

    [Fact]
    public void Zip_Extract_Encrypted_WithoutPassword_Throws()
    {
        CreateFile("src/secret.txt", "secret-content");
        var zip = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip, password: "pw-123"));

        Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(Extraction(zip, NewDestination())));
    }

    [Fact]
    public void Zip_Extract_SplitVolumes_AreRejoined()
    {
        var payload = new byte[4096];
        new Random(7).NextBytes(payload);
        var source = PathOf("data.bin");
        File.WriteAllBytes(source, payload);
        var zip = PathOf("split.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(source)], CreateSettings(zip, volumeSize: OneKb));

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination));

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(destination, "data.bin")));
    }

    // ---------- 重名策略 ----------

    [Fact]
    public void Zip_Extract_OverwritePolicy_Overwrites()
    {
        CreateFile("src/a.txt", "NEW");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        File.WriteAllText(Path.Combine(destination, "a.txt"), "OLD");
        var result = ArchiveExtractor.Extract(Extraction(zip, destination));

        Assert.Equal("NEW", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void Zip_Extract_SkipPolicy_KeepsExistingFile()
    {
        CreateFile("src/a.txt", "NEW");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        File.WriteAllText(Path.Combine(destination, "a.txt"), "OLD");
        var result = ArchiveExtractor.Extract(
            Extraction(zip, destination) with { Overwrite = OverwritePolicy.Skip });

        Assert.Equal("OLD", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal(1, result.SkippedCount);
        Assert.Empty(result.ExtractedFiles);
    }

    [Fact]
    public void Zip_Extract_RenamePolicy_CreatesNumberedCopy()
    {
        CreateFile("src/a.txt", "NEW");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        File.WriteAllText(Path.Combine(destination, "a.txt"), "OLD");
        ArchiveExtractor.Extract(Extraction(zip, destination) with { Overwrite = OverwritePolicy.Rename });

        Assert.Equal("OLD", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal("NEW", ReadText(Path.Combine(destination, "a (2).txt")));
    }

    [Fact]
    public void Zip_Extract_AskPolicy_DelegatesToResolver()
    {
        CreateFile("src/a.txt", "NEW");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        File.WriteAllText(Path.Combine(destination, "a.txt"), "OLD");
        var asked = new List<string>();
        var settings = Extraction(zip, destination) with
        {
            Overwrite = OverwritePolicy.Ask,
            DuplicateResolver = name =>
            {
                asked.Add(name);
                return DuplicateDecision.Skip;
            },
        };

        var result = ArchiveExtractor.Extract(settings);

        Assert.Equal(["a.txt"], asked);
        Assert.Equal("OLD", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void Zip_Extract_AskPolicy_WithoutResolver_RenamesInsteadOfOverwriting()
    {
        CreateFile("src/a.txt", "NEW");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        File.WriteAllText(Path.Combine(destination, "a.txt"), "OLD");
        ArchiveExtractor.Extract(Extraction(zip, destination) with { Overwrite = OverwritePolicy.Ask });

        Assert.Equal("OLD", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal("NEW", ReadText(Path.Combine(destination, "a (2).txt")));
    }

    // ---------- 智能解压（FR-3.3） ----------

    [Fact]
    public void Zip_Extract_SmartExtract_StripsSingleRootFolder()
    {
        CreateFile("src/payload.txt", "P");
        var zip = PathOf("smart.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(zip) with { IncludeRootFolder = true });

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination, smart: true));

        Assert.Equal("P", ReadText(Path.Combine(destination, "payload.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "src", "payload.txt")));
    }

    [Fact]
    public void Zip_Extract_WithoutSmartExtract_KeepsRootFolder()
    {
        CreateFile("src/payload.txt", "P");
        var zip = PathOf("smart.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(zip) with { IncludeRootFolder = true });

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination));

        Assert.Equal("P", ReadText(Path.Combine(destination, "src", "payload.txt")));
    }

    [Fact]
    public void Zip_Extract_SmartExtract_KeepsStructureWhenTopLevelFilesExist()
    {
        var a = CreateFile("a.txt", "A");
        CreateFile("src/b.txt", "B");
        var zip = PathOf("mixed.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(a), ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(zip));

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination, smart: true));

        Assert.Equal("A", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal("B", ReadText(Path.Combine(destination, "src", "b.txt")));
    }

    // ---------- TAR / TGZ / GZ ----------

    [Fact]
    public void Tar_Extract_RoundTrips()
    {
        CreateFile("src/a.txt", "A");
        CreateFile("src/sub/b.txt", "B");
        var tar = PathOf("out.tar");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(tar, "TAR"));

        var destination = NewDestination();
        var result = ArchiveExtractor.Extract(Extraction(tar, destination));

        Assert.Equal("A", ReadText(Path.Combine(destination, "a.txt")));
        Assert.Equal("B", ReadText(Path.Combine(destination, "sub", "b.txt")));
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public void Tgz_Extract_RoundTrips()
    {
        CreateFile("src/a.txt", "TGZ-CONTENT");
        var tgz = PathOf("out.tgz");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(tgz, "TGZ"));

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(tgz, destination));

        Assert.Equal("TGZ-CONTENT", ReadText(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public void Tar_Extract_SmartExtract_StripsSingleRootFolder()
    {
        CreateFile("src/payload.txt", "P");
        var tar = PathOf("smart.tar");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(tar, "TAR") with { IncludeRootFolder = true });

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(tar, destination, smart: true));

        Assert.Equal("P", ReadText(Path.Combine(destination, "payload.txt")));
    }

    [Fact]
    public void Gz_Extract_UsesArchiveNameWithoutExtension()
    {
        var file = CreateFile("note.txt", "GZ-CONTENT");
        var gz = PathOf("note.txt.gz");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], CreateSettings(gz, "GZ"));

        var destination = NewDestination();
        var result = ArchiveExtractor.Extract(Extraction(gz, destination));

        Assert.Equal("GZ-CONTENT", ReadText(Path.Combine(destination, "note.txt")));
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void Gz_Extract_HonorsOriginalFileNameFromHeader()
    {
        var gz = PathOf("named.gz");
        WriteGzipWithFileName(gz, "inner-name.txt", "HEADER-NAME");
        var destination = NewDestination();

        ArchiveExtractor.Extract(Extraction(gz, destination));

        Assert.Equal("HEADER-NAME", ReadText(Path.Combine(destination, "inner-name.txt")));
    }

    /// <summary>手工构造带 FNAME 的 gzip（RFC 1952）。</summary>
    private static void WriteGzipWithFileName(string path, string fileName, string content)
    {
        using var stream = File.Create(path);
        stream.Write([0x1F, 0x8B, 0x08, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03]);
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        stream.Write(nameBytes);
        stream.WriteByte(0);

        var payload = Encoding.UTF8.GetBytes(content);
        using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc.Update(payload);
        stream.Write(BitConverter.GetBytes((uint)crc.Value));
        stream.Write(BitConverter.GetBytes((uint)payload.Length));
    }

    // ---------- 安全与异常 ----------

    [Fact]
    public void Zip_Extract_ZipSlipEntry_Throws()
    {
        var zip = PathOf("slip.zip");
        using (var stream = File.Create(zip))
        using (var writer = new SharpZipOutputStream(stream))
        {
            writer.PutNextEntry(new SharpZipEntry("../evil.txt"));
            var payload = Encoding.UTF8.GetBytes("evil");
            writer.Write(payload, 0, payload.Length);
            writer.CloseEntry();
        }

        var destination = NewDestination();
        Assert.ThrowsAny<InvalidDataException>(() => ArchiveExtractor.Extract(Extraction(zip, destination)));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")), "逃逸文件不得写出目标目录");
    }

    [Fact]
    public void Zip_Extract_InvalidFileNameCharacters_AreSanitized()
    {
        var zip = PathOf("invalid.zip");
        using (var stream = File.Create(zip))
        using (var writer = new SharpZipOutputStream(stream))
        {
            writer.PutNextEntry(new SharpZipEntry("a?b.txt"));
            var payload = Encoding.UTF8.GetBytes("X");
            writer.Write(payload, 0, payload.Length);
            writer.CloseEntry();
        }

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination));

        Assert.Equal("X", ReadText(Path.Combine(destination, "a_b.txt")));
    }

    [Fact]
    public void Extract_UnsupportedFormat_ThrowsConstraint()
    {
        var file = PathOf("archive.7z");
        File.WriteAllText(file, "not an archive");

        var ex = Assert.Throws<ArchiveConstraintException>(
            () => ArchiveExtractor.Extract(Extraction(file, NewDestination())));

        Assert.Equal(ArchiveConstraintReason.FormatNotSupported, ex.Reason);
    }

    [Fact]
    public void Extract_MissingArchive_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => ArchiveExtractor.Extract(Extraction(PathOf("missing.zip"), NewDestination())));
    }

    [Fact]
    public void Extract_DeleteArchiveAfterExtraction_RemovesSource()
    {
        CreateFile("src/a.txt", "A");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var destination = NewDestination();
        ArchiveExtractor.Extract(Extraction(zip, destination) with { DeleteArchiveAfterExtraction = true });

        Assert.False(File.Exists(zip));
        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public void Extract_Cancelled_Throws()
    {
        CreateFile("src/a.txt", "A");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => ArchiveExtractor.Extract(Extraction(zip, NewDestination()), cts.Token));
    }

    // ---------- 密码探测（解压前提示「输入密码」） ----------

    [Theory]
    [InlineData(false)] // ZipCrypto
    [InlineData(true)]  // AES-256
    public void IsPasswordProtected_TrueForEncryptedZip(bool aes)
    {
        CreateFile("src/a.txt", "A");
        var zip = PathOf("enc.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(zip, password: "pw-123") with
            {
                Encryption = aes ? EncryptionMethodOption.Aes256 : EncryptionMethodOption.ZipCrypto,
            });

        Assert.True(ArchiveExtractor.IsPasswordProtected(zip));
    }

    [Fact]
    public void IsPasswordProtected_TrueForEncryptedSplitZip()
    {
        var source = PathOf("data.bin");
        var payload = new byte[4096];
        new Random(11).NextBytes(payload);
        File.WriteAllBytes(source, payload);
        var zip = PathOf("enc-split.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            CreateSettings(zip, password: "pw-123", volumeSize: OneKb));

        Assert.True(ArchiveExtractor.IsPasswordProtected(zip));
    }

    [Fact]
    public void IsPasswordProtected_FalseForPlainZipAndTar()
    {
        CreateFile("src/a.txt", "A");
        var zip = PathOf("plain.zip");
        var tar = PathOf("plain.tar");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(tar, "TAR"));

        Assert.False(ArchiveExtractor.IsPasswordProtected(zip));
        Assert.False(ArchiveExtractor.IsPasswordProtected(tar));
    }

    [Fact]
    public void IsPasswordProtected_FalseForMissingOrCorruptFile()
    {
        Assert.False(ArchiveExtractor.IsPasswordProtected(PathOf("missing.zip")));

        var corrupt = PathOf("corrupt.zip");
        File.WriteAllText(corrupt, "not a zip");
        Assert.False(ArchiveExtractor.IsPasswordProtected(corrupt));
    }
}