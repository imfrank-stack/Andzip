namespace Andzip.Core.Archive;

/// <summary>
/// 压缩方法选项（FR-2.5 高级设置）。
/// <para>
/// <see cref="EngineMethod"/> 直接就是引擎接受的方法名（"LZMA2" / "Deflate" / "BZip2"…），
/// 因此旧实现里的 ZIP 规范编号 <c>MethodCode</c> 被去掉了 —— 那是 ZIP 专属的概念，
/// 放在跨格式的接口上说不通。
/// </para>
/// <para>方法名不翻译（它们是专有名词），所以 <see cref="TextKey"/> 只对历史遗留的
/// “存储 / Deflate”两项保留，其余项由 UI 直接显示 <see cref="DisplayName"/>。</para>
/// </summary>
public sealed record CompressionMethodOption(string EngineMethod, string DisplayName, string? TextKey = null)
{
    /// <summary>交给引擎自己决定（不下发 -mm= 参数）。</summary>
    public static readonly CompressionMethodOption Auto = new(string.Empty, "Auto");

    public static readonly CompressionMethodOption Store = new("Copy", "Copy", "TEXT_METHOD_STORE");
    public static readonly CompressionMethodOption Deflate = new("Deflate", "Deflate", "TEXT_METHOD_DEFLATE");
    public static readonly CompressionMethodOption Deflate64 = new("Deflate64", "Deflate64");
    public static readonly CompressionMethodOption BZip2 = new("BZip2", "BZip2");
    public static readonly CompressionMethodOption Lzma = new("LZMA", "LZMA");
    public static readonly CompressionMethodOption Lzma2 = new("LZMA2", "LZMA2");
    public static readonly CompressionMethodOption Ppmd = new("PPMd", "PPMd");

    /// <summary>
    /// 各可创建格式实际能写的方法，顺序即下拉框顺序（第一项 = 默认）。
    /// <para>
    /// 只有“真的能选”的格式才列方法。tar / gz / bz2 / xz 是单流或纯打包格式，
    /// 下发 -mm= 会被引擎拒绝（Win32 87 / E_INVALIDARG），所以它们的列表是空的 ——
    /// UI 因此自动不显示方法下拉，而不是显示一个假的选项。
    /// </para>
    /// 这份表已经过 native 引擎实测：ZIP 的 BZip2/LZMA 与 7z 的 LZMA2/LZMA/PPMd/BZip2/Copy
    /// 都能真实往返（见 README 的自检记录）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CompressionMethodOption>> Table =
        new Dictionary<string, IReadOnlyList<CompressionMethodOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZIP"] = [Deflate, Store, BZip2, Lzma, Lzma2, Deflate64, Ppmd],
            ["7Z"] = [Lzma2, Lzma, Ppmd, BZip2, Deflate, Store],
            // 无可选方法：GZ / TGZ / BZ2 / XZ / TAR / WIM
        };

    /// <summary>该格式可选的方法；未知格式返回空列表（表示无可选项）。</summary>
    public static IReadOnlyList<CompressionMethodOption> ForFormat(ArchiveFormatOption format)
        => format.CreateFormat is null
            ? []
            : Table.TryGetValue(format.Id, out var methods) ? methods : [];

    /// <summary>兼容旧调用点的“全局默认”。</summary>
    public static CompressionMethodOption Default { get; } = Deflate;

    /// <summary>该格式下的默认方法（列表首项）；无列表时返回 <see cref="Auto"/>。</summary>
    public static CompressionMethodOption DefaultFor(ArchiveFormatOption format)
        => ForFormat(format).FirstOrDefault() ?? Auto;
}
