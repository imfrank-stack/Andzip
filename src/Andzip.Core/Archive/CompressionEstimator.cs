using System.Diagnostics;
using Andzip.Native;

namespace Andzip.Core.Archive;

/// <summary>
/// 「压完会有多大、大概要多久」的预估。做法只有一条，而且它不是猜比例表：
/// <strong>取一份样本，用产品自己的写出路径压一遍</strong> —— 同一个
/// <see cref="NativeEngine.BuildCreateOptions"/>、同一个编码器、同一个级别 ——
/// 再拿实测比例外推到总量。所以这个数天然带着这个格式自己的结构开销，
/// 而且和真跑出来的那一份同源于同一个实现。
/// <para>
/// 它是<strong>预估</strong>，界面上必须把"预估"两个字写出来。已知的偏差有三条，
/// 都是故意的：样本只取若干文件的头尾，内容很杂的输入（一半视频一半文本）会偏；
/// 固实块把互不相干的样本文件压进同一块，比例通常会比真实包略好；
/// 条目极多时包头没算全（每条几十字节，一万条才差几百 KB —— 仍然比"没有数"有用）。
/// </para>
/// </summary>
public static class CompressionEstimator
{
    /// <summary>一次预估的实测结果。字节数与速率都是量出来的，只有总量是外推的。</summary>
    /// <param name="InputBytes">待压的总字节（未压缩）。</param>
    /// <param name="OutputBytes">预估的压缩后字节。</param>
    /// <param name="SampledBytes">真的压进去的样本字节 —— 这个数越小，下面两个结论越虚。</param>
    /// <param name="BytesPerSecond">样本实测吞吐。它不含真实任务里的磁盘争用与被占用文件重试，
    /// 所以是个上界；界面要说"预计"而不是"就会这么久"。</param>
    public sealed record Prediction(long InputBytes, long OutputBytes, long SampledBytes, long BytesPerSecond)
    {
        /// <summary>压缩后占原大小的比例。不可压的内容（已经压好的包、视频）会略大于 1，
        /// 因为包头本身就是开销 —— 这不是 bug，是实话。</summary>
        public double Ratio => InputBytes > 0 ? (double)OutputBytes / InputBytes : 1.0;

        /// <summary>按实测吞吐外推的耗时。测不出速率（样本太小、时钟粒度不够）时为 Zero。</summary>
        public TimeSpan Duration => BytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)InputBytes / BytesPerSecond)
            : TimeSpan.Zero;

        /// <summary>输入小到一个样本就盖住了全部时，这份数是实测而不是外推。</summary>
        public bool IsMeasured => SampledBytes >= InputBytes;
    }

    /// <summary>单格预估的样本预算：4 MiB 够让 LZMA2 的字典吃饱，又不让人等。</summary>
    const long SingleBudget = 4 << 20;

    /// <summary>一张方法表各压一遍时的预算。11 个方法 × 4 MiB 会让人怀疑程序卡住了，
    /// 而这张表格要的是"谁比谁小"这个排序，不是每格的第三位小数。</summary>
    const long TableBudget = 1 << 20;

    /// <summary>最多开多少个文件去取样。再多每个只剩零头，再少一份样本代表不了整包。</summary>
    const int MaxSampleFiles = 16;

    /// <summary>每个被选中的文件至少取这么多，否则头尾各半就只剩几个字节。</summary>
    const int MinBytesPerFile = 64 << 10;

    /// <summary>按 <paramref name="settings"/> 里选定的方法与级别预估。</summary>
    public static Prediction? Estimate(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        CancellationToken cancellationToken = default)
        => EstimateWithBudget(inputs, settings, SingleBudget, cancellationToken);

    /// <summary>
    /// 把这个格式能选的方法各压一遍样本，供"每种压缩方式都要给个大小"那张表用。
    /// 顺序按格式自己的方法表（<see cref="ArchiveFormatOption.Methods"/>），界面照这个顺序排就行。
    /// 某个方法失败时那一格给 null 而不是整张表作废 —— 少一行比全空有用。
    /// </summary>
    public static IReadOnlyList<(CompressionMethodOption Method, Prediction? Prediction)> EstimateMethods(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<(CompressionMethodOption, Prediction?)>();
        foreach (var method in settings.ResolvedFormat.Methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add((method, EstimateWithBudget(
                inputs,
                settings with { Method = method },
                TableBudget,
                cancellationToken)));
        }

        return rows;
    }

    private static Prediction? EstimateWithBudget(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        long budget,
        CancellationToken cancellationToken)
    {
        var format = settings.ResolvedFormat;
        if (format.CreateFormat is null)
        {
            return null;
        }

        try
        {
            var plan = ArchivePlanBuilder.Build(inputs, settings, settings.OutputPath);
            if (plan.Files.Count == 0 || plan.TotalInputBytes <= 0)
            {
                return null;
            }

            var samples = TakeSamples(plan, budget);
            if (samples.Count == 0)
            {
                return null;
            }

            // 卷大小对"压多大"没有影响（它只是把同一条字节流分几个文件装），
            // 而内存写出配卷大小是内核明确拒绝的组合 —— 带着它来问一次就抛一次。
            var options = NativeEngine.BuildCreateOptions(format.CreateFormat, settings, progress: null)
                with { VolumeSize = null };

            var clock = Stopwatch.StartNew();
            byte[] archive;
            using (var writer = ArchiveWriter.CreateInMemory(options))
            {
                foreach (var (entryPath, data) in samples)
                {
                    writer.AddData(data, entryPath);
                }

                archive = writer.FinishToBytes();
            }
            clock.Stop();

            var sampled = samples.Sum(static sample => (long)sample.Data.Length);
            if (archive.Length == 0 || sampled == 0)
            {
                return null;
            }

            var estimate = (long)Math.Round(plan.TotalInputBytes * ((double)archive.Length / sampled));
            var speed = clock.Elapsed.TotalSeconds > 0 ? (long)(sampled / clock.Elapsed.TotalSeconds) : 0L;
            return new Prediction(plan.TotalInputBytes, Math.Max(estimate, 1L), sampled, speed);
        }
        catch (Exception ex) when (ex is AndzipException or ArchiveConstraintException
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 预估是锦上添花：引擎没装、方法被这个格式拒、目录读不动，都只让那一格空着，
            // 不该在用户按下"开始"之前先弹一个他从没要求的错误。别把这条放宽到 Exception ——
            // 那样的话一个真 bug 也会安静地变成"没有数"。
            return null;
        }
    }

    /// <summary>
    /// 挑样本：<strong>每个文件分到的样本字节数与它对总量的贡献成正比</strong>，段内取头尾。
    /// <para>
    /// 平均分配是错的，而且错得很难看：一个 60 MB 可压的文本加一个 20 MB 随机数据，
    /// 各取一半样本得到的是"这两种内容各占一半"的比例 0.53，而真实包里它们是 3:1 = 0.30 ——
    /// 预估会大到离谱。按大小加权之后样本与整包同成分，外推才站得住。
    /// </para>
    /// <para>
    /// 从最大的开始排是第二道保险：份额低于地板的小文件会被抬到
    /// <see cref="MinBytesPerFile"/>，先排大文件就不会让几十个零碎文件把预算吃光。
    /// 头尾各取一半的道理同上 —— 只看开头会把"前半是文本、后半是已压好的数据"那种文件
    /// 估小，只看结尾会被包尾的索引结构骗到。
    /// </para>
    /// </summary>
    private static IReadOnlyList<(string EntryPath, byte[] Data)> TakeSamples(ArchivePlan plan, long budget)
    {
        var samples = new List<(string EntryPath, byte[] Data)>();
        if (plan.TotalInputBytes <= 0)
        {
            return samples;
        }

        var spent = 0L;
        foreach (var file in plan.Files.OrderByDescending(static entry => entry.Size))
        {
            if (file.Size <= 0 || spent >= budget || samples.Count >= MaxSampleFiles)
            {
                break;
            }

            var room = budget - spent;
            // 整个输入比预算还小时（拷几个文档是常见情形）这个式子就等于"全取"。
            var share = (long)Math.Round((double)budget * file.Size / plan.TotalInputBytes);
            var floor = Math.Min(MinBytesPerFile, file.Size);
            var take = (int)Math.Min(Math.Max(share, floor), Math.Min(room, file.Size));
            if (take <= 0)
            {
                continue;
            }

            var data = ReadHeadAndTail(file.SourcePath, file.Size, take);
            if (data.Length > 0)
            {
                samples.Add((file.EntryPath, data));
                spent += data.Length;
            }
        }

        return samples;
    }

    /// <summary>读出前一半与后一半，中间跳过。文件比要取的还短时就是整个读。</summary>
    private static byte[] ReadHeadAndTail(string sourcePath, long size, int take)
    {
        if (size <= take)
        {
            try
            {
                return File.ReadAllBytes(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<byte>();
            }
        }

        var head = take / 2;
        var tail = take - head;
        var buffer = new byte[take];
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.ReadExactly(buffer, 0, head);
            stream.Seek(size - tail, SeekOrigin.Begin);
            stream.ReadExactly(buffer, head, tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 这个文件读不动（被独占、刚被删、在别的盘上掉线）：换下一个，而不是让整格空着。
            return Array.Empty<byte>();
        }

        return buffer;
    }
}
