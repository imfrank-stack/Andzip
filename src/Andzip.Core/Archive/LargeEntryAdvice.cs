namespace Andzip.Core.Archive;

/// <summary>
/// 「这一批内容用 ZIP 压会很慢，换 7z 快得多」这一句该不该说。
/// <para>
/// 原因不在编码器好坏，在并行的<strong>单位</strong>：ZIP 的多线程是按<strong>条目</strong>切的
/// （上游 <c>ZipUpdate.cpp</c> 把线程数截断到待压文件个数，<c>numZipThreads_limit = numFilesToCompress</c>），
/// 一个目录里几百个小文件已经吃满多核；而 7z/LZMA2 是按<strong>块</strong>切的，
/// 一个大文件自己就能摊满核。所以同样的字节量，<strong>单个大条目压成 ZIP 只有一核在跑</strong>
/// —— 实测：同样的 512 MiB，压成一个文件比拆成 16 个慢 <strong>6.9–9.2 倍</strong>，
/// 数字与复现命令都在 <c>docs/perf-baseline.md</c>。
/// </para>
/// <para>
/// 这条建议只做一件事：说出来，并给一键切过去的手。它<strong>不</strong>替用户改选择，
/// 因为 ZIP 常常是别人要求的交付格式，不是性能选择。
/// </para>
/// </summary>
public static class LargeEntryAdvice
{
    /// <summary>
    /// 单个条目大到多少字节就值得说一句。
    /// <para>
    /// 这个数是<strong>定的，不是量出来的</strong>，所以要说清楚它凭什么：几十 MiB 的单条目，
    /// 单核 ZIP 也就一两秒，提示变成噪音；到了几百 MiB，人就开始盯着进度条了，
    /// 而这一段恰恰是"只有一核在跑"最能要回来的地方。256 MiB 取的是这一段的下沿。
    /// 真正量过的是上面那句"慢多少倍"。
    /// </para>
    /// </summary>
    public const long ThresholdBytes = 256L * 1024 * 1024;

    /// <summary>
    /// 该建议换格式吗？只有 ZIP 会问：TAR 根本不压，gzip/bz2/xz 本来就是单流单条目，
    /// 7z 自己就是答案。
    /// </summary>
    /// <param name="format">用户此刻选的格式。</param>
    /// <param name="largestEntryBytes">这批输入里最大的<strong>单个文件</strong>的字节数
    /// （文件夹会展开到文件，因为进了包里那就是一个个条目）。</param>
    public static bool SuggestSevenZip(ArchiveFormatOption format, long largestEntryBytes)
        => format.IsZip && largestEntryBytes >= ThresholdBytes;
}
