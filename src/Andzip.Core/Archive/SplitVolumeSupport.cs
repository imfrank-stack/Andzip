namespace Andzip.Core.Archive;

/// <summary>
/// 分卷支持（FR-2.3）。ZIP 采用 WinZip 风格命名：`name.z01, name.z02, …, name.zip`（`.zip` 为最后一卷，
/// 存在 `name.z01` 即视为分卷包）；分卷即完整 ZIP 字节流按卷大小的顺序切分——按卷序拼接后与单文件 ZIP 等价。
/// 当前仅 ZIP 生成分卷（其他格式待与参考软件命名核对后补充）。
/// </summary>
internal static class SplitVolumeSupport
{
    /// <summary>第 index 卷（自 1 起）的路径；超过 99 卷时自然扩展（z100…）。</summary>
    public static string PartPath(string archivePath, int index) => $"{BasePath(archivePath)}.z{index:D2}";

    /// <summary>归档路径去掉扩展名的部分（分卷文件的共同前缀）。</summary>
    public static string BasePath(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(full);
        var stem = Path.GetFileNameWithoutExtension(full);
        return string.IsNullOrEmpty(directory) ? stem : Path.Combine(directory, stem);
    }

    /// <summary>该路径是不是 <c>name.zNN</c> 形式的中间卷（末卷 <c>name.zip</c> 不算）。</summary>
    public static bool IsVolumePart(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length == 4
               && extension[0] == '.'
               && extension[1] == 'z'
               && char.IsDigit(extension[2])
               && char.IsDigit(extension[3]);
    }

    /// <summary>把完整归档按卷大小切分为分卷文件；返回实际生成的文件（各卷 + 末卷 `.zip`，按卷序）。</summary>
    public static IReadOnlyList<string> Split(
        string sourcePath,
        string archivePath,
        long volumeSize,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var total = new FileInfo(sourcePath).Length;
        if (overwrite)
        {
            ArchiveOutputGuard.TryDelete(archivePath);
        }

        if (total <= volumeSize)
        {
            File.Copy(sourcePath, archivePath, overwrite: false);
            return [archivePath];
        }

        var created = new List<string>();
        try
        {
            using var source = File.OpenRead(sourcePath);
            var buffer = new byte[81920];
            long remaining = total;
            var index = 1;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isLast = remaining <= volumeSize;
                var partPath = isLast ? archivePath : PartPath(archivePath, index);
                var partSize = isLast ? remaining : volumeSize;

                using (var part = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var left = partSize;
                    while (left > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0)
                        {
                            throw new IOException("Unexpected end of the archive while splitting into volumes.");
                        }

                        part.Write(buffer, 0, read);
                        left -= read;
                    }
                }

                created.Add(partPath);
                remaining -= partSize;
                index++;
            }
        }
        catch
        {
            TryDeleteAll(created);
            throw;
        }

        return created;
    }

    /// <summary>枚举分卷包的全部卷（按拼接顺序）；非分卷返回仅含归档本身的单元素列表。</summary>
    public static IReadOnlyList<string> EnumerateParts(string archivePath)
    {
        var parts = new List<string>();
        for (var index = 1; ; index++)
        {
            var partPath = PartPath(archivePath, index);
            if (!File.Exists(partPath))
            {
                break;
            }

            parts.Add(partPath);
        }

        if (parts.Count == 0)
        {
            return [archivePath];
        }

        parts.Add(archivePath);
        return parts;
    }

    /// <summary>按卷序拼接为单个文件（供完整性测试与后续解压复用）。</summary>
    public static void Concatenate(IReadOnlyList<string> parts, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        foreach (var part in parts)
        {
            using var source = File.OpenRead(part);
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>覆盖写入前清理同前缀的旧分卷（避免上次运行残留的 z01… 混入本次结果）。</summary>
    public static void DeleteStaleParts(string archivePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var stem = Path.GetFileName(BasePath(archivePath));
        foreach (var file in Directory.EnumerateFiles(directory, stem + ".z*"))
        {
            var suffix = Path.GetFileName(file)[(stem.Length + 1)..];
            if (suffix.Length >= 2 && suffix[0] == 'z' && suffix[1..].All(char.IsAsciiDigit))
            {
                ArchiveOutputGuard.TryDelete(file);
            }
        }
    }

    public static void TryDeleteAll(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            ArchiveOutputGuard.TryDelete(path);
        }
    }
}