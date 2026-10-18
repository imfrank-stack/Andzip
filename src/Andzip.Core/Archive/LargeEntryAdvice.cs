namespace Andzip.Core.Archive;

/// <summary>
/// 「里面有一个很大的单个文件，换成 7z 更小」这一句该不该说。
/// <para>
/// 前半句的原因在并行的<strong>单位</strong>：ZIP 的多线程是按<strong>条目</strong>切的
/// （上游 <c>ZipUpdate.cpp</c> 把线程数截断到待压文件个数，<c>numZipThreads_limit = numFilesToCompress</c>），
/// 一个目录里几百个小文件已经吃满多核，而<strong>单个大条目压成 ZIP 只有一核在跑</strong>
/// —— 同样的 512 MiB，压成一个文件比拆成 16 个慢 <strong>6.9–9.2 倍</strong>，
/// 数字与复现命令都在 <c>docs/perf-baseline.md</c>（那一组是 5.7.0 换 Deflate 编码器之前量的，
/// 换过之后没重测，见 <c>docs/perf-p2c-deflate-swap.md</c> 第 9 节）。
/// </para>
/// <para>
/// 但「换 7z 会快得多」这句在 5.8.0 被推翻了：128 MiB 单条目、同一个混合语料，
/// ZIP/标准/Deflate <strong>0.55 s</strong>（一核），7z/标准/LZMA2 即便按块摊到 <strong>12.52 核</strong>
/// 也要 <strong>1.28 s</strong>；极限档差得更远（1.06 s 对 18.03 s）。数字在
/// <c>docs/perf-tiers.tsv</c> 与 <c>docs/perf-mt.tsv</c>。所以这一句现在卖的是<strong>体积</strong>：
/// 同一格 0.29641 → 0.27354，小 7.7%。
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
    /// 这个数是<strong>定的，不是量出来的</strong>：几十 MiB 的单条目，压完也就一秒上下，
    /// 提示变成噪音；到了几百 MiB，人就开始盯着进度条了，体积差（上面那 7.7%）
    /// 也才值得为它动一次格式。256 MiB 取的是这一段的下沿。
    /// 这一档往下挪或往上调都需要一个新量法（"多大才值得换格式"是偏好，不是性能），
    /// 所以现在不动它。
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
