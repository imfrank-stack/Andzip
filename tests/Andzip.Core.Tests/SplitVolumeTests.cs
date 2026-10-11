using System.IO.Compression;
using Andzip.Core.Archive;
using SharpStringCodec = ICSharpCode.SharpZipLib.Zip.StringCodec;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Andzip.Core.Tests;

/// <summary>分卷压缩（FR-2.3）：WinZip 风格分卷命名（z01…zip）、按卷序拼接等价单文件、清理与失败处理。</summary>
public class SplitVolumeTests : IDisposable
{
    private const long OneKb = 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-split-").FullName;

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

    /// <summary>写入伪随机内容（避免高压缩比导致产物小于一个分卷而无法产生多卷）。</summary>
    private string CreateRandomFile(string relativePath, int size)
    {
        var full = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var data = new byte[size];
        new Random(12345).NextBytes(data);
        File.WriteAllBytes(full, data);
        return full;
    }

    private static ArchiveCreationSettings Settings(string output, long? volumeSize, string? password = null)
        => new(output, CompressionLevelOption.Default, Password: password, VolumeSizeBytes: volumeSize);

    /// <summary>枚举 z01…zNN 分卷（不含末卷 .zip 本身）。</summary>
    private static string[] VolumePartsOf(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        var stem = Path.GetFileNameWithoutExtension(full);
        return Directory.EnumerateFiles(Path.GetDirectoryName(full)!, stem + ".z*")
            .Where(p => !string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertPartsFollowWinZipNaming(string archivePath, long volumeSize, out int partCount)
    {
        var fullArchive = Path.GetFullPath(archivePath);
        var volumes = VolumePartsOf(archivePath);
        Assert.True(volumes.Length >= 1, "应至少产生一个 z01 分卷");

        for (var i = 0; i < volumes.Length; i++)
        {
            var expected = Path.Combine(
                Path.GetDirectoryName(fullArchive)!,
                $"{Path.GetFileNameWithoutExtension(fullArchive)}.z{i + 1:D2}");
            Assert.Equal(expected, volumes[i]);
            Assert.Equal(volumeSize, new FileInfo(volumes[i]).Length); // 每个分卷（不含末卷）都等于分卷大小
        }

        Assert.True(File.Exists(fullArchive), "末卷应为 .zip");
        Assert.InRange(new FileInfo(fullArchive).Length, 1, volumeSize);
        partCount = volumes.Length + 1;
    }

    private static Dictionary<string, string> ReadEntries(string zipPath, string? password = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (password is null)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                result[entry.FullName] = reader.ReadToEnd();
            }

            return result;
        }

        using var encrypted = new SharpZipFile(zipPath, SharpStringCodec.FromCodePage(65001)) { Password = password };
        foreach (SharpZipEntry entry in encrypted)
        {
            if (!entry.IsFile)
            {
                continue;
            }

            using var stream = encrypted.GetInputStream(entry);
            using var reader = new StreamReader(stream);
            result[entry.Name] = reader.ReadToEnd();
        }

        return result;
    }

    /// <summary>把分卷按卷序拼接为一个临时 zip（模拟解压工具的读取方式；测试自行实现以保持黑盒）。</summary>
    private string ConcatenateParts(string archivePath)
    {
        var destination = PathOf("concatenated.zip");
        using (var target = File.Create(destination))
        {
            foreach (var part in VolumePartsOf(archivePath).Append(Path.GetFullPath(archivePath)))
            {
                using var source = File.OpenRead(part);
                source.CopyTo(target);
            }
        }

        return destination;
    }

    // ---------- 选项表 ----------

    [Fact]
    public void VolumeOptions_MatchReferenceList()
    {
        Assert.Equal(14, VolumeOption.All.Count);
        Assert.Equal(Enumerable.Range(0, 14), VolumeOption.All.Select(o => o.Id));
        Assert.Null(VolumeOption.Default.SizeBytes);
        Assert.Null(VolumeOption.All[0].SizeBytes);
        Assert.False(VolumeOption.All[0].IsCustom);
        Assert.True(VolumeOption.All[^1].IsCustom);
        Assert.Equal(25L * 1024 * 1024, VolumeOption.FromId(6).SizeBytes);   // 25 MB (Gmail)
        Assert.Equal(4L * 1024 * 1024 * 1024, VolumeOption.FromId(11).SizeBytes); // 4 GB (FAT32)
        Assert.Equal(3L * 1024 * 1024, VolumeOption.CreateCustom(3).SizeBytes);
        Assert.True(VolumeOption.CreateCustom(3).IsCustom);
    }

    // ---------- 分卷创建 ----------

    [Fact]
    public void Create_MultiVolume_UsesWinZipNamingAndVolumeSize()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");

        var result = ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb));

        AssertPartsFollowWinZipNaming(output, OneKb, out var partCount);
        Assert.True(partCount >= 4, "4 KB 随机数据在 1 KB 分卷下应至少产生 4 个文件（含末卷）");
        // 分卷时 CreatedArchives 列出实际落盘的每个文件，末卷（可打开的那个）在最后。
        Assert.Equal(VolumePartsOf(output).Append(Path.GetFullPath(output)).ToArray(), result.CreatedArchives.ToArray());
        Assert.Equal(
            VolumePartsOf(output).Append(Path.GetFullPath(output)).Sum(p => new FileInfo(p).Length),
            result.OutputBytes);
    }

    [Fact]
    public void Create_MultiVolume_ConcatenationEqualsSingleArchive()
    {
        var file = CreateRandomFile("note.bin", 5000);
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb));

        var entries = ReadEntries(ConcatenateParts(output));
        var content = Assert.Contains("note.bin", entries);
        Assert.Equal(File.ReadAllText(file), content);
    }

    [Fact]
    public void Create_OutputFitsInSingleVolume_ProducesPlainZipWithoutParts()
    {
        var file = CreateRandomFile("small.bin", 256);
        var output = PathOf("out.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, 10 * 1024 * 1024));

        Assert.True(File.Exists(output));
        Assert.Empty(VolumePartsOf(output));
        Assert.Single(ReadEntries(output));
    }

    [Fact]
    public void Create_MultiVolume_Encrypted_RoundTripsWithPassword()
    {
        var file = CreateRandomFile("secret.bin", 4096);
        var output = PathOf("enc.zip");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb, password: "P@ss-split"));

        AssertPartsFollowWinZipNaming(output, OneKb, out _);
        var entries = ReadEntries(ConcatenateParts(output), "P@ss-split");
        Assert.Contains("secret.bin", entries.Keys);
    }

    [Fact]
    public void Create_MultiVolume_CleansStalePartsFromPreviousRun()
    {
        var file = CreateRandomFile("small.bin", 128);
        var output = PathOf("out.zip");
        File.WriteAllText(PathOf("out.z01"), "stale");
        File.WriteAllText(PathOf("out.z02"), "stale");

        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, 10 * 1024 * 1024));

        Assert.True(File.Exists(output));
        Assert.Empty(VolumePartsOf(output));
    }

    [Fact]
    public void Create_MultiVolume_Cancelled_LeavesNoParts()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb), cts.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(VolumePartsOf(output));
    }

    [Fact]
    public void Create_MultiVolume_FailureCleansCreatedParts()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");
        Directory.CreateDirectory(PathOf("out.z02")); // 阻塞第 2 卷写入，制造失败

        Assert.ThrowsAny<Exception>(
            () => ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb)));

        Assert.False(File.Exists(output));
        Assert.False(File.Exists(PathOf("out.z01")));
        Assert.False(File.Exists(PathOf("out.z03")));
    }

    // ---------- 完整性测试（分卷） ----------

    [Fact]
    public void Tester_SplitArchive_Passes()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb));

        ArchiveTester.Test(output);
    }

    [Fact]
    public void Tester_SplitEncryptedArchive_PassesWithPassword()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("enc.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb, password: "pw-123"));

        ArchiveTester.Test(output, "pw-123");
    }

    [Fact]
    public void Tester_SplitArchive_DetectsCorruptedVolume()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb));

        var firstPart = VolumePartsOf(output)[0];
        using (var stream = new FileStream(firstPart, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            var value = stream.ReadByte();
            stream.Position = stream.Length / 2;
            stream.WriteByte((byte)(value ^ 0xFF)); // 破坏首个分卷中的压缩数据
        }

        Assert.ThrowsAny<Exception>(() => ArchiveTester.Test(output));
    }

    [Fact]
    public void Tester_SplitArchive_ThrowsWhenFirstVolumeMissing()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");
        ArchiveCreator.Create([ArchiveInputItem.FromPath(file)], Settings(output, OneKb));

        File.Delete(VolumePartsOf(output)[0]);

        Assert.ThrowsAny<Exception>(() => ArchiveTester.Test(output));
    }

    // ---------- 门面组合 ----------

    [Fact]
    public void Facade_Split_TestAfterCreation_Passes()
    {
        var file = CreateRandomFile("data.bin", 4096);
        var output = PathOf("out.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, OneKb) with { TestAfterCreation = true });

        Assert.True(File.Exists(output));
        Assert.True(File.Exists(PathOf("out.z01")));
    }

    [Fact]
    public void Facade_Split_DeleteSource_RemovesSourceAndKeepsVolumes()
    {
        var file = CreateRandomFile("gone.bin", 4096);
        var output = PathOf("out.zip");

        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(file)],
            Settings(output, OneKb) with { DeleteSourceAfterCreation = true });

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(PathOf("out.z01")));
    }
}