namespace Andzip.Core.Archive;

/// <summary>一次操作进度（创建 / 解压 / 测试 / 打开）。</summary>
public enum ArchiveOperation
{
    Open,
    Compress,
    Extract,
    Test,
    Scan,
    Hash,
}

/// <summary>
/// 引擎上报的一次进度。
/// <para>
/// 投递线程：进度回调来自引擎的解码器工作线程，不是调用线程。设置端一律传
/// <see cref="Progress{T}"/>（它在构造时捕获当前的 SynchronizationContext），
/// 这样回调会自动回到 UI 线程；Core 自身不做任何线程编组。
/// </para>
/// <para>总量未知时 <see cref="TotalBytes"/> 为 0，<see cref="Fraction"/> 为 null，
/// 此时 UI 应退回不确定进度条。</para>
/// </summary>
public sealed record ArchiveProgress(
    ArchiveOperation Operation,
    long DoneBytes,
    long TotalBytes,
    string? CurrentEntry,
    int EntryIndex,
    int EntryCount)
{
    /// <summary>完成比例 0..1；总量未知时为 null。</summary>
    public double? Fraction => TotalBytes > 0 ? Math.Min(1.0, (double)DoneBytes / TotalBytes) : null;

    /// <summary>完成百分比 0..100；总量未知时为 null。</summary>
    public int? Percent => Fraction is { } fraction ? (int)Math.Round(fraction * 100.0) : null;
}
