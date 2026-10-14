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
    /// 下面五个是 7-Zip 的<strong>扩展方法</strong>（来自 7-Zip-zstd / NanaZip 那一系），
    /// 只在 7z 里可选 —— 标准安装的 7-Zip 不一定带这些编码器，这种包在别人机器上可能打不开，
    /// 所以选中它们时界面上要另说一句话（<c>TEXT_METHOD_EXTENSION_NOTE</c>）。
    /// <para>它们进表的前提是<strong>实测能写也能读回来</strong>，不是"内核登记了这个编码器"：
    /// 2.2.0 之前这里全部缺席，因为当时写 7z 一律 <c>E_INVALIDARG</c>（记录见 docs/开发记录.md）。
    /// 现在五种方法 × 五档级别（0/1/3/5/9）共 25 组、外加带字典大小 / 带口令 / 非固实 / 多输入
    /// 共 20 组都做了 create → test → extract → 逐字节比对，全部一致；
    /// 这条证明已经固化成常驻检查（<c>Andzip.Native.Tests</c>），不再靠人手跑 CLI。</para>
    /// </summary>
    public static readonly CompressionMethodOption Zstd = new("ZSTD", "Zstandard");
    public static readonly CompressionMethodOption Lz4 = new("LZ4", "LZ4");
    public static readonly CompressionMethodOption Lz5 = new("LZ5", "LZ5");
    public static readonly CompressionMethodOption Lizard = new("LIZARD", "Lizard");
    public static readonly CompressionMethodOption Brotli = new("BROTLI", "Brotli");

    /// <summary>这五种只给 7z 用；界面上要按它们另说一句话，红线上说得更明白。</summary>
    public static bool IsExtension(CompressionMethodOption method)
        => method == Zstd || method == Lz4 || method == Lz5 || method == Lizard || method == Brotli;

    /// <summary>
    /// 各可创建格式实际能写的方法，顺序即下拉框顺序（第一项 = 默认）。
    /// <para>
    /// 只有“真的能选”的格式才列方法。tar / gz / bz2 / xz 是单流或纯打包格式，
    /// 下发 -mm= 会被引擎拒绝（Win32 87 / E_INVALIDARG），所以它们的列表是空的 ——
    /// UI 因此自动不显示方法下拉，而不是显示一个假的选项。
    /// </para>
    /// <para>
    /// 非标准方法永不进 ZIP 是一条与内核无关的红线：那种 ZIP 用的是非标准方法号，
    /// WinRAR / 资源管理器 / 原版 7-Zip 都打不开，而用户会把这种包发给别人。
    /// 这条现在由<strong>两边各守一遍</strong>：这里不列，内核也拒（实测
    /// <c>azcore create x.zip -f zip -m LZ4</c> 回 <c>E_NOTIMPL</c>，留下一个 0 字节文件）——
    /// 只靠界面列不列，等于把红线交给"没人改坏这一行"。
    /// 顺带一提，原版 7-Zip 25 能<em>读</em> ZIP 里的 Zstandard 却写不出来 —— 单向兼容更容易骗人。
    /// </para>
    /// 这份表已经过 native 引擎实测：ZIP 的 BZip2/LZMA 与 7z 的 LZMA2/LZMA/PPMd/BZip2/Copy
    /// 都能真实往返（见 README 的自检记录），7z 那五个扩展方法见 <see cref="Zstd"/> 那段。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CompressionMethodOption>> Table =
        new Dictionary<string, IReadOnlyList<CompressionMethodOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZIP"] = [Deflate, Store, BZip2, Lzma, Lzma2, Deflate64, Ppmd],
            ["7Z"] = [Lzma2, Lzma, Ppmd, BZip2, Deflate, Zstd, Lz4, Lz5, Lizard, Brotli, Store],
            // 无可选方法：GZ / TGZ / BZ2 / XZ / TAR / WIM
        };

    /// <summary>该格式下的方法；未知格式返回空列表（表示无可选项）。</summary>
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
