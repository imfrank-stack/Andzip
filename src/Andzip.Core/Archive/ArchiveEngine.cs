namespace Andzip.Core.Archive;

/// <summary>
/// native 压缩引擎的进程级信息与控制项。
/// <para>
/// 存在的理由：GUI 需要知道“引擎在不在、是什么版本、支持多少格式”来显示状态栏与标题，
/// 也需要在压缩前设定线程数/内存上限。这些都不属于任何一次具体操作，所以单独一个门面。
/// </para>
/// <para>
/// 线程数与内存上限必须在打开任何归档句柄之前设置，对之后的操作才生效。
/// </para>
/// </summary>
public static class ArchiveEngine
{
    /// <summary>引擎（andzip_core 动态库）是否可用。false 时所有压缩/解压都会抛
    /// <see cref="ArchiveFailureException"/>（Reason = EngineMissing）。</summary>
    public static bool IsAvailable => NativeEngine.IsAvailable;

    /// <summary>不可用时的原因（含尝试过的加载路径），可用于状态栏提示。</summary>
    public static string? UnavailableReason => NativeEngine.MissingDetail;

    /// <summary>例如 “7-Zip 26.04 (x64)”；引擎缺失时为 null。</summary>
    public static string? EngineName => NativeEngine.Build?.Engine;

    /// <summary>构建配置（Release / Debug）。</summary>
    public static string? BuildConfiguration => NativeEngine.Build?.BuildConfig;

    /// <summary>引擎可读的格式数量。</summary>
    public static int ReadableFormatCount => NativeEngine.Formats.Count(f => f.CanRead);

    /// <summary>当前构建可创建的格式数量。</summary>
    public static int WritableFormatCount => NativeEngine.Formats.Count(f => f.CanWrite);

    /// <summary>引擎可用编解码器数量。</summary>
    public static int CodecCount => NativeEngine.Codecs.Count;

    /// <summary>
    /// 多线程编码器使用的线程数；0 表示“每个硬件线程一个”。
    /// 引擎缺失时读写都安全地退化为 0 / 无操作。
    /// </summary>
    public static uint NumThreads
    {
        get => NativeEngine.NumThreads;
        set => NativeEngine.NumThreads = value;
    }

    /// <summary>解码器可分配内存上限（MiB）；0 表示不限制。</summary>
    public static ulong MemoryLimitMiB
    {
        get => NativeEngine.MemoryLimitMiB;
        set => NativeEngine.MemoryLimitMiB = value;
    }

    /// <summary>
    /// 拼接 WinZip 风格分卷时放临时文件的目录；null 或空表示系统临时目录。
    /// <para>
    /// 与 <see cref="NumThreads"/> 一样是进程级设置：它服务的对象（<c>SingleFileArchive</c>）
    /// 藏在门面实现里，界面拿不到实例，只能靠这一处入口。它是"设置 → 高级"里那一项的落点。
    /// </para>
    /// </summary>
    public static string? TempDirectory { get; set; }

    /// <summary>
    /// 真正要写临时文件时用的目录。指定的目录不可用（被删、没权限、压根不是目录）时
    /// 退回系统临时目录而不是抛异常 —— 分卷拼接失败会让用户打不开一个本来能打开的包，
    /// 而那跟临时目录设在哪儿比起来是件小事得多得多的事。
    /// </summary>
    public static string ResolveTempDirectory()
    {
        var configured = TempDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetTempPath();
        }

        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch (Exception)
        {
            return Path.GetTempPath();
        }
    }

    /// <summary>状态栏/标题用的一行摘要。</summary>
    public static string Describe()
        => IsAvailable
            ? $"{EngineName} · {BuildConfiguration} · {ReadableFormatCount} formats / {WritableFormatCount} writable"
            : "compression engine unavailable";
}
