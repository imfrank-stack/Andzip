namespace Andzip.Core.Archive;

/// <summary>
/// 压缩级别（FR-2.4）。
/// <para>
/// 五档对应引擎 <c>-mx=</c> 的 0 / 1 / 3 / 5 / 9。档位之间的取舍是有意分开的：
/// 前三档（仅存储 / 极速 / 快速）拼的是速度，体积不是它们的目标；标准档要的是速度与
/// 压缩率的平衡；极限档只管压得小，花多少时间不算。所以级别不是"同一个算法多跑几遍"，
/// 而是三套不同的评判标准 —— 排序也按这个来，越靠前越快。
/// </para>
/// <para>
/// <see cref="EngineLevel"/> 是引擎的 0..9 级。<c>System.IO.Compression.CompressionLevel</c>
/// 不再出现在这里 —— 那是旧托管实现泄漏到接口上的类型，换引擎后已无意义。
/// </para>
/// <para>方法名不翻译（专有名词），级别名进语言包（它们是界面文案）。</para>
/// </summary>
/// <param name="Id">稳定标识，也是界面下拉的顺序；存进设置文件的是它，所以只增不改。</param>
/// <param name="TextKey">语言包键。</param>
/// <param name="EngineLevel">引擎的 0..9 级；0 表示仅存储。</param>
public sealed record CompressionLevelOption(int Id, string TextKey, int EngineLevel)
{
    public static readonly CompressionLevelOption Store =
        new(0, "TEXT_COMPRESSIONMETHOD_STORE", EngineLevel: 0);

    public static readonly CompressionLevelOption UltraFast =
        new(1, "TEXT_COMPRESSIONMETHOD_ULTRAFAST", EngineLevel: 1);

    public static readonly CompressionLevelOption Fast =
        new(2, "TEXT_COMPRESSIONMETHOD_FAST", EngineLevel: 3);

    public static readonly CompressionLevelOption Normal =
        new(3, "TEXT_COMPRESSIONMETHOD_NORMAL", EngineLevel: 5);

    public static readonly CompressionLevelOption Max =
        new(4, "TEXT_COMPRESSIONMETHOD_MAX", EngineLevel: 9);

    private static readonly CompressionLevelOption[] Levels = [Store, UltraFast, Fast, Normal, Max];

    public static IReadOnlyList<CompressionLevelOption> All => Levels;

    public static CompressionLevelOption Default { get; } = Normal;

    /// <summary>下拉框里的位置。<see cref="Id"/> 是持久化标识，两者将来未必再相等，所以不拿它当索引。</summary>
    public static int PositionOf(CompressionLevelOption level) => Array.IndexOf(Levels, level);

    /// <summary>
    /// 按存储的 <c>Id</c> 取级别。
    /// <para>认不出来时回落到 <see cref="Normal"/> 而不是抛：<c>Id</c> 会写进
    /// <c>settings.ini</c>，而绿色版可以换一个版本再跑 —— 老程序读到新版本写下的级别号，
    /// 应该按"最保守的默认"继续工作，而不是启动就崩。与
    /// <c>SettingKeys.ParseDoubleClick</c> 同一个道理。</para>
    /// </summary>
    public static CompressionLevelOption FromId(int id)
        => All.FirstOrDefault(o => o.Id == id) ?? Normal;

    /// <summary>级别存进设置文件用的稳定标识。</summary>
    public int ToStorage() => Id;
}
