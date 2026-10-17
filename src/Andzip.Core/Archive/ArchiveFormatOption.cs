namespace Andzip.Core.Archive;

/// <summary>
/// 新建压缩文件对话框里的一个格式候选（FR-2.2）。
/// <para>
/// 列表本身是固定的（顺序与参考软件一致，避免下拉框里冒出 60 行），但**能力**不再写死：
/// <see cref="CanCreate"/> 与 <see cref="CanRead"/> 由 native 引擎在运行时回答，
/// 所以打开 7Z / XZ / BZ2 / WIM 的创建按钮不再需要改代码就能点亮。
/// </para>
/// <para>
/// 注意：读取路径完全不依赖本类型 —— 引擎自己按内容嗅探格式。本表只服务于
/// “创建”对话框与“打开”对话框的文件类型过滤器。
/// </para>
/// </summary>
/// <param name="Id">界面显示与设置持久化用的短标识（"ZIP" / "7Z" / "TGZ"…）。</param>
/// <param name="Extension">主扩展名，含点，小写。</param>
/// <param name="CreateFormat">引擎的规范格式名（"zip" / "7z" / "tar" / "gzip"…）；
/// null 表示本工具不创建该格式，UI 里可选但不可开始。</param>
/// <param name="CreateWrapper">复合格式的容器包装（仅 TGZ 用："tar" 再 "gzip"）。</param>
public sealed record ArchiveFormatOption(
    string Id,
    string Extension,
    string? CreateFormat,
    string? CreateWrapper = null,
    bool PasswordCapable = false,
    bool HeaderEncryptionCapable = false,
    bool VolumeCapable = false)
{
    /// <summary>
    /// 候选列表。顺序对齐参考软件实测（FR-2.2 / 附录 D.1），并按需求文档保留只读项。
    /// </summary>
    public static IReadOnlyList<ArchiveFormatOption> All { get; } =
    [
        new("ZIP", ".zip", "zip", PasswordCapable: true, VolumeCapable: true),
        new("ZIPX", ".zipx", null),
        new("EXE", ".exe", null),
        new("TAR", ".tar", "tar"),
        new("TGZ", ".tgz", "tar", CreateWrapper: "gzip"),
        new("LZH", ".lzh", null),
        new("ISO", ".iso", null),
        new("7Z", ".7z", "7z", PasswordCapable: true, HeaderEncryptionCapable: true, VolumeCapable: true),
        new("GZ", ".gz", "gzip"),
        new("BZ2", ".bz2", "bzip2"),
        new("XZ", ".xz", "xz"),
        new("ZPAQ", ".zpaq", null),
    ];

    public static ArchiveFormatOption Default { get; } = All[0];

    /// <summary>
    /// 「新建压缩文件」对话框能列出来的格式 —— 引擎此刻真的写得出来的那些。
    /// <para>
    /// <see cref="All"/> 里有五项（ZIPX / EXE / LZH / ISO / ZPAQ）是照参考软件抄来的**只读**候选，
    /// 选中之下只能开始一句「该功能暂未实现」。用户的话是"列出来就一定要实现"，
    /// 所以列表按 <see cref="CanCreate"/> 过滤，而不是摆着让人点。
    /// </para>
    /// <para>
    /// 按运行时能力过滤而不是写死一张子表：哪天引擎能写 ISO 了，那一行自己就回来，不用改代码。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ArchiveFormatOption> Creatable => [.. All.Where(format => format.CanCreate)];

    /// <summary>引擎里用于「方法 / 级别 / 加密」查询的格式名。只读项为 null。</summary>
    public string? EngineFormat => CreateFormat;

    /// <summary>是否复合创建（先写内层容器，再整体压成外层流）。目前只有 TGZ。</summary>
    public bool IsCompound => CreateWrapper is not null;

    /// <summary>该格式能否由当前构建的引擎创建（引擎缺失时为 false）。</summary>
    public bool CanCreate => CreateFormat is not null && NativeEngine.CanCreateWith(CreateFormat);

    /// <summary>该格式能否被引擎读取（用于只读项的灰显判断）。</summary>
    public bool CanRead => CreateFormat is not null && NativeEngine.CanReadWith(CreateFormat);

    /// <summary>是否支持密码保护（FR-2.6）。仍需引擎确实可写。</summary>
    public bool SupportsPassword => CanCreate && PasswordCapable;

    /// <summary>是否支持非 ASCII 密码。7z 用 UTF-16 派生密钥，支持；
    /// ZIP 的写入端在 7-Zip 里被限制为 OEM 代码页可表示的字符（IsSimpleAsciiString），
    /// 非 ASCII 密码会被直接拒绝，所以这里提前报错而不是丢一个 Win32 87。</summary>
    public bool SupportsNonAsciiPassword => SupportsPassword
        && !string.Equals(CreateFormat, "zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否支持分卷（FR-2.3）。两种实现，不是一种：7z 由<strong>引擎逐卷写</strong>
    /// （<c>name.7z.001…</c>，7-Zip 自己的命名，别的软件也认），ZIP 由 <strong>Core 事后切块</strong>
    /// （WinZip 的 <c>name.z01…name.zip</c>）。命名与"哪一卷是入口"的差别见 <see cref="SplitVolumeSupport"/>。
    /// </summary>
    public bool SupportsVolume => CanCreate && VolumeCapable;

    /// <summary>是否支持加密文件名（7z 专属：连文件列表一起加密）。</summary>
    public bool SupportsEncryptHeaders => CanCreate && HeaderEncryptionCapable;

    /// <summary>是否 7z。固实块（solid）是 7z 独有的概念，只有它才接受这个设置。</summary>
    public bool IsSevenZip => string.Equals(CreateFormat, "7z", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>是否 ZIP。两件事按 ZIP 分家：多线程的并行单位是条目（见 <see cref="LargeEntryAdvice"/>），
    /// 以及非标准方法号会让别人打不开（见 <see cref="CompressionMethodOption"/>）。</summary>
    public bool IsZip => string.Equals(CreateFormat, "zip", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>是否有高级设置可看（FR-2.5）：可选方法多于一个，或可加密。</summary>
    public bool SupportsAdvancedSettings => CanCreate && (Methods.Count > 1 || SupportsPassword);

    /// <summary>该格式可选的压缩方法（引擎侧方法名）。</summary>
    public IReadOnlyList<CompressionMethodOption> Methods => ForFormat(this);

    public static IReadOnlyList<CompressionMethodOption> ForFormat(ArchiveFormatOption format)
        => CompressionMethodOption.ForFormat(format);

    /// <summary>“打开”对话框的文件类型过滤器扩展名集合（引擎实际可读的扩展名）。</summary>
    public static IReadOnlyList<string> OpenableExtensions => NativeEngine.ReadableExtensions();

    /// <summary>
    /// 这个路径像不像一个"引擎读得开的包"。
    /// <para>只看扩展名，<strong>不</strong>嗅探内容 —— 用户把东西拖到窗口上的那一刻，
    /// 为了判断类型去开一次文件是不划算的，而且加密包连列表都要口令。
    /// 真按内容走的是打开那条路（引擎自己会报读不了）。</para>
    /// <para>目录永远不算：拖进来的文件夹是要压的东西，不是要看的东西。</para>
    /// </summary>
    public static bool IsReadablePath(string path)
    {
        if (path.Length == 0 || Directory.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);

        return extension.Length > 1 && OpenableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static ArchiveFormatOption FromId(string id)
        => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown archive format id.");

    /// <summary>按扩展名（含点，大小写无关）匹配候选格式；未知扩展名返回 null。</summary>
    public static ArchiveFormatOption? FromExtension(string extension)
        => All.FirstOrDefault(f => string.Equals(f.Extension, extension, StringComparison.OrdinalIgnoreCase));
}
