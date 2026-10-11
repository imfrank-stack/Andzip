namespace Andzip.Core.Archive;

/// <summary>
/// 压缩级别选项（对齐参考软件的 4 档，FR-2.4 / 附录 C.5）：
/// 0-仅存储 / 1-快速压缩 / 2-正常压缩（默认）/ 3-极限压缩。
/// <para>
/// <see cref="EngineLevel"/> 是引擎的 0..9 级。<c>System.IO.Compression.CompressionLevel</c>
/// 不再出现在这里 —— 那是旧托管实现泄漏到接口上的类型，换引擎后已无意义。
/// </para>
/// </summary>
public sealed record CompressionLevelOption(int Id, string TextKey, int EngineLevel)
{
    public static readonly CompressionLevelOption Store =
        new(0, "TEXT_COMPRESSIONMETHOD_STORE", EngineLevel: 0);

    public static readonly CompressionLevelOption Fast =
        new(1, "TEXT_COMPRESSIONMETHOD_FAST", EngineLevel: 3);

    public static readonly CompressionLevelOption Normal =
        new(2, "TEXT_COMPRESSIONMETHOD_NORMAL", EngineLevel: 5);

    public static readonly CompressionLevelOption Max =
        new(3, "TEXT_COMPRESSIONMETHOD_MAX", EngineLevel: 9);

    public static IReadOnlyList<CompressionLevelOption> All { get; } = [Store, Fast, Normal, Max];

    public static CompressionLevelOption Default { get; } = Normal;

    public static CompressionLevelOption FromId(int id)
        => All.FirstOrDefault(o => o.Id == id)
           ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown compression level id.");
}
