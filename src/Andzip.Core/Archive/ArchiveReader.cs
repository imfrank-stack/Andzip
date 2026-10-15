using NativeArchiveReader = Andzip.Native.ArchiveReader;

namespace Andzip.Core.Archive;

/// <summary>
/// 压缩包内的一个条目（供压缩包视图展示，FR-4.2）。
/// <para>
/// <see cref="Method"/> 是引擎报告的压缩方法名（"LZMA2:24" / "Deflate" / "Copy"…），
/// 取代了旧的 ZIP 规范编号 + <c>NoZipMethod = -1</c> 占位值 —— 后者对 tar / gz 这类
/// 没有“ZIP 压缩方法”概念的格式无法表达。未知时为 null。
/// </para>
/// </summary>
/// <param name="Path">相对路径，恒用 <c>/</c> 分隔，目录以 <c>/</c> 结尾。</param>
/// <param name="Name">路径最后一段（目录为目录名，不含结尾斜杠）。</param>
/// <param name="Index">条目在清单里的序号，也是回读原始字节（缩略图）时的句柄内序号。
/// 由 <see cref="ArchiveContentSession"/> 使用；-1 表示来源不是本次读取的清单。</param>
/// <param name="Crc">引擎报告的 CRC32；格式未存储时为 null（目录通常没有）。</param>
/// <param name="Attributes">Win32 文件属性；格式未存储时为 null。</param>
/// <param name="PosixMode">POSIX 权限位，0 表示格式未按 Unix 语义写入（用于推断“附加信息”里的 OS）。</param>
/// <param name="Comment">条目注释，无则 null。</param>
public sealed record ArchiveEntryInfo(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    long CompressedSize,
    bool IsEncrypted,
    string? Method,
    DateTime? Modified,
    int Index = -1,
    uint? Crc = null,
    uint? Attributes = null,
    uint PosixMode = 0,
    string? Comment = null);

/// <summary>压缩包内容清单（压缩包视图的数据源；计数与体积对应状态栏 FR-4.3）。</summary>
/// <param name="ArchiveSize">压缩包自身占用的字节数（状态栏的“压缩文件大小”），未知时为 0。</param>
/// <param name="FormatName"><strong>文件本身</strong>的格式（引擎给的规范名：<c>zip</c> / <c>7z</c> / <c>tar</c> /
/// <c>gzip</c> …）。嵌套展开时这里仍是外层那个 —— 「编辑」那几项能不能用、注释那一项灰不灰，
/// 判据都是"文件本身是什么"而不是"清单里的成员来自哪一层"。</param>
public sealed record ArchiveListing(
    string ArchivePath,
    IReadOnlyList<ArchiveEntryInfo> Entries,
    int FileCount,
    int DirectoryCount,
    long TotalSize,
    long TotalCompressedSize,
    string? Comment,
    long ArchiveSize = 0,
    string? FormatName = null);

/// <summary>
/// 压缩包清单读取（FR-4 压缩包视图的数据源）：仅解析目录结构，不解压数据。
/// <para>
/// 格式由引擎按内容嗅探，不再有扩展名 switch —— 因此 60 种可读格式（zip / 7z / tar / gz / xz /
/// bzip2 / wim / cab / iso / nsis / rpm / ntfs / dmg / vhdx …）都无需 Core 改动即可列出。
/// </para>
/// <para>
/// 嵌套透明化：外层只含一个 <c>*.tar</c> 成员时（.tgz / .tar.gz / .tar.bz2 …）自动展开为内层清单，
/// 取代旧的 “TGZ 走 tar+gzip 专用分支”。
/// </para>
/// </summary>
public static class ArchiveReader
{
    public static ArchiveListing Read(
        string archivePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive to open was not found.", fullPath);
        }

        // 头加密的 7z 在不给口令时，引擎只能报成"不是压缩包"；不改判的话界面会说
        // 「该格式不支持」，用户连口令框都等不到。详见 EncryptedHeaderProbe。
        try
        {
            return NativeEngine.Run("read archive", () =>
            {
                // 7-Zip 风格的分卷引擎自己处理；WinZip 的 .z01 需要先拼成单文件。
                using var single = SingleFileArchive.Open(fullPath, cancellationToken);
                using var reader = OpenNative(single.FilePath, password, out var outerFormat);
                return BuildListing(reader, fullPath, outerFormat);
            }, badFormatMeansUnsupported: true);
        }
        catch (Exception ex) when (EncryptedHeaderProbe.LooksLikeMissingPassword(ex, fullPath))
        {
            throw new ArchiveFailureException(
                ArchiveFailureReason.Password,
                "这个包的内容是加密的，需要口令。",
                ex);
        }
    }

    /// <summary>
    /// 打开归档并解析嵌套。清单、解压、完整性测试三条路径都从这里取句柄，
    /// 所以 .tgz / .tar.gz 在三个操作上行为一致（否则解压会只落出一个 .tar 文件）。
    /// </summary>
    internal static NativeArchiveReader OpenNative(string fullPath, string? password)
        => OpenNative(fullPath, password, out _);

    /// <summary>
    /// 同 <see cref="OpenNative(string, string?)"/>，另外交回<strong>文件本身</strong>的格式名。
    /// <para>嵌套展开（.tar.gz 这类）之后句柄指向内层，内外层格式不是一回事，
    /// 所以这个名字必须在展开之前取；「编辑」与「压缩文件注释」判的都是外层。</para>
    /// </summary>
    internal static NativeArchiveReader OpenNative(string fullPath, string? password, out string? outerFormat)
    {
        var outer = NativeArchiveReader.Open(fullPath, NativeEngine.BuildOpenOptions(password, progress: null));

        var inner = TryUnwrapTar(outer, fullPath, password);
        if (inner is null)
        {
            outerFormat = outer.FormatName;
            return outer;
        }

        outerFormat = outer.FormatName;
        outer.Dispose();
        return inner;
    }

    /// <summary>
    /// 外层是单流压缩格式（gzip / bzip2 / xz / zstd）且只有一个成员时，尝试把成员当成
    /// 归档再展开一层 —— 这就是 .tgz / .tar.gz / .tar.xz 的常见形态。
    /// <para>
    /// 判据用外层格式而不是成员文件名：成员名由写入方决定（TGZ 的中间文件名带 .tar 只是习惯），
    /// 而“外层是单流压缩器”是引擎告诉我们的事实。展开失败就按单文件清单呈现。
    /// </para>
    /// </summary>
    private static NativeArchiveReader? TryUnwrapTar(NativeArchiveReader outer, string fullPath, string? password)
    {
        if (outer.EntryCount != 1 || outer.Entries[0].IsDirectory)
        {
            return null;
        }

        if (!IsSingleStreamCompressor(outer.FormatName))
        {
            return null;
        }

        var only = outer.Entries[0];

        // 只有体积合理时才展开，避免把一个巨大的内层归档整体读进内存。
        const ulong limit = 512ul * 1024 * 1024;
        if (only.HasSize && only.Size > limit)
        {
            return null;
        }

        try
        {
            var inner = outer.ReadEntry(0);
            return NativeArchiveReader.Open(inner, nameHint: fullPath, NativeEngine.BuildOpenOptions(password, null));
        }
        catch (Andzip.Native.AndzipException)
        {
            // 成员本身不是归档（一个普通的 README.gz）：按单文件呈现。
            return null;
        }
    }

    private static readonly string[] SingleStreamCompressors = ["gzip", "bzip2", "xz", "zstd"];

    private static bool IsSingleStreamCompressor(string? formatName)
        => formatName is not null
           && SingleStreamCompressors.Contains(formatName, StringComparer.OrdinalIgnoreCase);

    private static ArchiveListing BuildListing(NativeArchiveReader reader, string archivePath, string? outerFormat)
    {
        var entries = new List<ArchiveEntryInfo>(reader.Entries.Count);
        int fileCount = 0, directoryCount = 0;
        long totalSize = 0, totalCompressed = 0;

        foreach (var entry in reader.Entries)
        {
            // 接口约定目录以 '/' 结尾，而引擎（尤其 7z/tar）不一定带尾斜杠，这里补齐。
            var path = entry.IsDirectory && !entry.Name.EndsWith('/')
                ? entry.Name + "/"
                : entry.Name;
            var displayPath = entry.IsDirectory ? path.TrimEnd('/') : path;
            var name = LastSegment(displayPath);

            var size = entry.Size is { } entrySize ? ToSigned(entrySize) : 0;
            var compressed = entry.PackedSize is { } packed ? ToSigned(packed) : 0;

            if (entry.IsDirectory)
            {
                directoryCount++;
            }
            else
            {
                fileCount++;
                totalSize += size;
                totalCompressed += compressed;
            }

            entries.Add(new ArchiveEntryInfo(
                path,
                name,
                entry.IsDirectory,
                size,
                compressed,
                entry.IsEncrypted,
                entry.Method,
                entry.ModifiedTime is { } modified ? modified.LocalDateTime : null,
                // 条目序号就是上面这个枚举的顺序，回读字节时按它寻址。
                (int)entry.Index,
                // 引擎用 0 表示“未存储 CRC”，而真正的 CRC32=0 几乎不存在；
                // 目录本来就没有 CRC，所以这里折成 null 让界面留空而不是显示 00000000。
                entry.Crc == 0 ? null : entry.Crc,
                entry.Attributes == 0 ? null : entry.Attributes,
                entry.PosixMode,
                entry.Comment));
        }

        return new ArchiveListing(
            archivePath,
            entries,
            fileCount,
            directoryCount,
            totalSize,
            totalCompressed,
            reader.Comment,
            ToSigned(reader.PhysicalSize),
            outerFormat);
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>引擎用 ulong.MaxValue 表示“未知”，已在 shell 侧折成 null；这里只收窄为 long。</summary>
    private static long ToSigned(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
