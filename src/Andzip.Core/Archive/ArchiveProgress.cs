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
/// 投递线程：进度回调来自引擎的解码器工作线程，不是调用线程。Core 自身不做任何线程编组，
/// 所以设置端传的 <see cref="IProgress{T}"/> 必须自己负责回到 UI 线程 ——
/// 界面那边用的是 <c>ProgressWindow.CreateSink()</c>，它显式走 Dispatcher。
/// <strong>不要换成裸 <c>new Progress&lt;T&gt;(...)</c></strong>：它捕获的是构造那一刻的
/// <c>SynchronizationContext</c>，环境里没人装过上下文时会退回 .NET 默认实现，
/// 而默认实现的 <c>Post</c> 就是把回调丢进线程池（0.9.11 的闪退就是这么来的）。
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
