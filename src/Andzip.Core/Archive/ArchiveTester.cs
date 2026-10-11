using NativeArchiveReader = Andzip.Native.ArchiveReader;

namespace Andzip.Core.Archive;

/// <summary>
/// 完整性测试（FR-3.6 “测试压缩包”）：解压并校验 CRC，但不落地任何文件。
/// <para>
/// 旧实现按扩展名分发，并对 ZIP 手工跑 CRC 循环；现在整个包交给引擎的
/// <c>test_only</c> 模式，因此 60 种可读格式一视同仁。数据损坏时引擎的错误码会被
/// <c>NativeEngine</c> 翻译成 <see cref="InvalidDataException"/>，与既有契约一致。
/// </para>
/// </summary>
public static class ArchiveTester
{
    public static void Test(
        string archivePath,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive to test was not found.", fullPath);
        }

        NativeEngine.Run("test archive", () =>
        {
            using var single = SingleFileArchive.Open(fullPath, cancellationToken);
            using var reader = ArchiveReader.OpenNative(single.FilePath, password);

            var options = NativeEngine.BuildExtractOptions(
                password,
                Andzip.Native.AzOverwrite.Overwrite,
                preserveMTime: false,
                progress,
                ArchiveOperation.Test);

            reader.Test(options);
            cancellationToken.ThrowIfCancellationRequested();
        }, badFormatMeansUnsupported: true);
    }
}
