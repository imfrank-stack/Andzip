namespace Andzip.Core.Archive;

/// <summary>
/// 分卷支持（FR-2.3）。两种命名，两条来源：
/// <list type="bullet">
/// <item><description>ZIP 用 WinZip 风格：<c>name.z01, name.z02, …, name.zip</c>，末卷 <c>name.zip</c>
/// 才是能被打开的那一个。这一种由 <see cref="Split"/> 把完整归档切成字节块 —— 引擎侧的卷机制
/// 不产这种命名。</description></item>
/// <item><description>7z 用 7-Zip 自己的风格：<c>name.7z.001, .002, …</c>，<strong>首卷</strong>
/// <c>.001</c> 就是能打开的那一个（内核按兄弟卷拼成一条流）。这一种整条路都在引擎里，
/// Core 只认命名：见 <see cref="SevenZipPartPath"/>。</description></item>
/// </list>
/// 两种都是"完整归档按卷大小顺序切分"，按卷序拼接后与单文件等价。
/// </summary>
internal static class SplitVolumeSupport
{
    /// <summary>第 index 卷（自 1 起）的路径；超过 99 卷时自然扩展（z100…）。</summary>
    public static string PartPath(string archivePath, int index) => $"{BasePath(archivePath)}.z{index:D2}";

    /// <summary>7z 那一族的第 index 卷：<c>name.7z</c> 变成 <c>name.7z.001</c>（三位，补零）。</summary>
    public static string SevenZipPartPath(string archivePath, int index) => $"{archivePath}.{index:D3}";

    /// <summary>归档路径去掉扩展名的部分（分卷文件的共同前缀）。</summary>
    public static string BasePath(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(full);
        var stem = Path.GetFileNameWithoutExtension(full);
        return string.IsNullOrEmpty(directory) ? stem : Path.Combine(directory, stem);
    }

    /// <summary>
    /// 这一卷是不是"要靠兄弟卷才能读、而且不是入口"的那一个。
    /// <para>两族的入口不同：WinZip 风格是末卷 <c>name.zip</c>，7z 风格是首卷
    /// <c>name.7z.001</c>（内核从它开始往后拼）。两处都不是"卷"，因为它们自己就能被打开 ——
    /// 「压缩后测试」与"删包"都按这个判断决定该动手的是哪一个。</para>
    /// </summary>
    public static bool IsVolumePart(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length != 4 || extension[0] != '.')
        {
            return false;
        }

        if (extension[1] == 'z' && char.IsAsciiDigit(extension[2]) && char.IsAsciiDigit(extension[3]))
        {
            return true;
        }

        return extension != ".001"
               && char.IsAsciiDigit(extension[1])
               && char.IsAsciiDigit(extension[2])
               && char.IsAsciiDigit(extension[3]);
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

    /// <summary>
    /// 7z 那一族的分卷（<c>name.7z.001 …</c>），由引擎自己写出来。没有 <c>.001</c> 时
    /// 说明它根本不是分卷，返回只有 <paramref name="archivePath"/> 自己。
    /// </summary>
    public static IReadOnlyList<string> EnumerateSevenZipParts(string archivePath)
    {
        var parts = new List<string>();
        for (var index = 1; index <= 999; index++)
        {
            var part = SevenZipPartPath(archivePath, index);
            if (!File.Exists(part))
            {
                break;
            }

            parts.Add(part);
        }

        return parts.Count > 0 ? parts : [archivePath];
    }

    /// <summary>
    /// 写之前扫掉同前缀的旧 7z 分卷。两件事逼出来的：引擎对已经存在的卷号直接报
    /// "文件已存在"（它是逐卷 CreateNew，不覆盖），而上次留下的 <c>.005</c> 会把这次的
    /// 三包结果变成"四包，最后一包是别人的旧数据"。
    /// </summary>
    public static void DeleteStaleSevenZipParts(string archivePath)
    {
        for (var index = 1; index <= 999; index++)
        {
            var part = SevenZipPartPath(archivePath, index);
            if (!File.Exists(part))
            {
                break;
            }

            ArchiveOutputGuard.TryDelete(part);
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