namespace Andzip.Core.Archive;

/// <summary>
/// 分卷大小选项（FR-2.3；档位与参考软件语言包实测一致）。
/// SizeBytes 为 null 表示不分卷；IsCustom 表示「自定义大小」（实际大小由 UI 输入的 MB 值决定）。
/// 换算取 1024 进制；「4.7 GB (DVD)」按 4.7 × 1024³ 取整（与参考软件的精确字节数待后续实测校准）。
/// </summary>
public sealed record VolumeOption(int Id, long? SizeBytes, string TextKey, bool IsCustom = false)
{
    private const long Mb = 1024L * 1024L;
    private const long Gb = 1024L * Mb;

    public static IReadOnlyList<VolumeOption> All { get; } =
    [
        new(0, null, "TEXT_VOLUME_NONE"),
        new(1, 3 * Mb, "TEXT_VOLUME_3MB"),
        new(2, 4 * Mb, "TEXT_VOLUME_4MB"),
        new(3, 5 * Mb, "TEXT_VOLUME_5MB"),
        new(4, 10 * Mb, "TEXT_VOLUME_10MB"),
        new(5, 20 * Mb, "TEXT_VOLUME_20MB"),
        new(6, 25 * Mb, "TEXT_VOLUME_25MB_GMAIL"),
        new(7, 100 * Mb, "TEXT_VOLUME_100MB"),
        new(8, 300 * Mb, "TEXT_VOLUME_300MB"),
        new(9, 650 * Mb, "TEXT_VOLUME_650MB_CD"),
        new(10, 700 * Mb, "TEXT_VOLUME_700MB_CD"),
        new(11, 4 * Gb, "TEXT_VOLUME_4GB_FAT32"),
        new(12, (long)(4.7 * Gb), "TEXT_VOLUME_47GB_DVD"),
        new(13, null, "TEXT_VOLUME_CUSTOM", IsCustom: true),
    ];

    public static VolumeOption Default { get; } = All[0];

    /// <summary>用「自定义大小」的 MB 值创建选项实例。</summary>
    public static VolumeOption CreateCustom(int sizeMb) => All[^1] with { SizeBytes = sizeMb * Mb };

    public static VolumeOption FromId(int id)
        => All.FirstOrDefault(o => o.Id == id)
           ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown volume option id.");
}