using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>压缩包清单读取（FR-4 压缩包视图数据源）：ZIP（含分卷 / 加密条目元数据）与 TAR / TGZ / GZ。</summary>
public class ArchiveReaderTests : IDisposable
{
    private const long OneKb = 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-read-").FullName;

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

    private static ArchiveCreationSettings CreateSettings(string output, string formatId = "ZIP", string? password = null, long? volumeSize = null)
        => new(
            output,
            CompressionLevelOption.Default,
            Format: ArchiveFormatOption.FromId(formatId),
            Password: password,
            VolumeSizeBytes: volumeSize);

    // ---------- ZIP ----------

    [Fact]
    public void Read_Zip_ListsEntriesWithSizesAndCounts()
    {
        CreateFile("src/a.txt", "AAAAA");
        CreateFile("src/sub/b.txt", "BB");
        Directory.CreateDirectory(PathOf("src", "empty"));
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var listing = ArchiveReader.Read(zip);

        Assert.Equal(2, listing.FileCount);
        Assert.Equal(2, listing.DirectoryCount); // sub/ 与 empty/
        Assert.Equal(7, listing.TotalSize);
        Assert.True(listing.TotalCompressedSize > 0);
        Assert.Equal(Path.GetFullPath(zip), listing.ArchivePath);

        var a = Assert.Single(listing.Entries, e => e.Path == "a.txt");
        Assert.Equal("a.txt", a.Name);
        Assert.False(a.IsDirectory);
        Assert.Equal(5, a.Size);
        Assert.False(a.IsEncrypted);

        var empty = Assert.Single(listing.Entries, e => e.Path == "empty/");
        Assert.True(empty.IsDirectory);
        Assert.Equal(0, empty.Size);

        var nested = Assert.Single(listing.Entries, e => e.Path == "sub/b.txt");
        Assert.Equal("b.txt", nested.Name);
    }

    [Fact]
    public void Read_Zip_ReportsCompressionMethod()
    {
        CreateFile("src/a.txt", "AAAAA");
        var zip = PathOf("store.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(PathOf("src"))],
            CreateSettings(zip) with { Method = CompressionMethodOption.Store });

        var listing = ArchiveReader.Read(zip);

        Assert.Equal("Store", Assert.Single(listing.Entries, e => !e.IsDirectory).Method);
    }

    [Fact]
    public void Read_Zip_EncryptedEntries_AreMarkedWithoutPassword()
    {
        CreateFile("src/a.txt", "secret");
        var zip = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip, password: "pw-123"));

        var listing = ArchiveReader.Read(zip);

        Assert.True(Assert.Single(listing.Entries, e => !e.IsDirectory).IsEncrypted);
    }

    [Fact]
    public void Read_Zip_SplitArchive_IsRejoined()
    {
        var payload = new byte[4096];
        new Random(3).NextBytes(payload);
        var source = PathOf("data.bin");
        File.WriteAllBytes(source, payload);
        var zip = PathOf("split.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(source)], CreateSettings(zip, volumeSize: OneKb));

        var listing = ArchiveReader.Read(zip);

        var entry = Assert.Single(listing.Entries);
        Assert.Equal("data.bin", entry.Name);
        Assert.Equal(4096, entry.Size);
    }

    // ---------- TAR / TGZ / GZ ----------

    [Fact]
    public void Read_Tar_ListsEntriesWithDirectoryFlag()
    {
        CreateFile("src/a.txt", "A");
        Directory.CreateDirectory(PathOf("src", "empty"));
        var tar = PathOf("out.tar");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(tar, "TAR"));

        var listing = ArchiveReader.Read(tar);

        Assert.Equal(1, listing.FileCount);
        Assert.Equal(1, listing.DirectoryCount);
        Assert.Equal(1, listing.TotalSize);
        // tar 按 512 字节记录存储，成员在盘上确实占 512 字节 —— 引擎报的是真实占用。
        Assert.Equal(512, listing.TotalCompressedSize);
        Assert.Null(Assert.Single(listing.Entries, e => !e.IsDirectory).Method);
    }

    [Fact]
    public void Read_Tgz_ListsEntries()
    {
        CreateFile("src/a.txt", "TGZ");
        var tgz = PathOf("out.tgz");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(tgz, "TGZ"));

        var listing = ArchiveReader.Read(tgz);

        var entry = Assert.Single(listing.Entries);
        Assert.Equal("a.txt", entry.Name);
        Assert.Equal(3, entry.Size);
    }

    [Fact]
    public void Read_Gzip_UsesArchiveNameAndOriginalSize()
    {
        var file = CreateFile("note.txt", "GZ-CONTENT");
        var gz = PathOf("note.txt.gz");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], CreateSettings(gz, "GZ"));

        var listing = ArchiveReader.Read(gz);

        var entry = Assert.Single(listing.Entries);
        Assert.Equal("note.txt", entry.Name);
        Assert.Equal("GZ-CONTENT".Length, entry.Size);
        Assert.False(entry.IsDirectory);
        Assert.Equal(1, listing.FileCount);
    }

    // ---------- 异常 ----------

    [Fact]
    public void ContentSession_ReadsEntryBytesByListedIndex()
    {
        // 缩略图靠「清单里的 Index」回读原始字节，这条就是那个契约。
        CreateFile("src/a.txt", "AAAAA");
        CreateFile("src/sub/b.txt", "BB");
        var zip = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(PathOf("src"))], CreateSettings(zip));

        var listing = ArchiveReader.Read(zip);
        Assert.True(listing.ArchiveSize > 0);

        using var session = ArchiveContentSession.Open(zip);
        foreach (var entry in listing.Entries.Where(e => !e.IsDirectory))
        {
            Assert.True(entry.Index >= 0);
            Assert.NotNull(entry.Crc);

            var bytes = session.ReadEntry(entry.Index, maxBytes: 1024);
            Assert.Equal(entry.Size, bytes?.LongLength ?? -1);
        }

        // 超出上限时拒绝而不是整体读进内存（一张 500 MB 的图不该被缩略图流程吞下）。
        var first = listing.Entries.First(e => !e.IsDirectory);
        Assert.Null(session.ReadEntry(first.Index, maxBytes: 1));
    }

    [Fact]
    public void Read_UnsupportedFormat_ThrowsConstraint()
    {
        var file = PathOf("archive.7z");
        File.WriteAllText(file, "not an archive");

        var ex = Assert.Throws<ArchiveConstraintException>(() => ArchiveReader.Read(file));

        Assert.Equal(ArchiveConstraintReason.FormatNotSupported, ex.Reason);
    }

    [Fact]
    public void Read_MissingArchive_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => ArchiveReader.Read(PathOf("missing.zip")));
    }
}