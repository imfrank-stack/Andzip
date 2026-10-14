using Andzip.Native;
using NativeArchiveReader = Andzip.Native.ArchiveReader;

namespace Andzip.Core.Archive;

/// <summary>一个没通过校验的成员，连同内核的原话（"CRC mismatch in '…'"）。</summary>
public sealed record TestFailure(string Name, string Reason);

/// <summary>
/// 整包测试的结论。
/// <para>
/// <see cref="Members"/> 与 <see cref="Verified"/> 只数<strong>有数据的</strong>成员：
/// 目录条目没有可校验的字节，把它算进"通过"里等于白送一个数。
/// </para>
/// <para>
/// <see cref="Unexamined"/> 是"这份报告不完整"的那个数 —— 坏成员多到封顶、或者内核报的名字
/// 对不上清单时它就不为零。宁可说"还有 N 个没查完"，也不要报一份看起来完整其实断了的答案。
/// </para>
/// </summary>
public sealed record ArchiveTestReport(
    int Members,
    int Verified,
    IReadOnlyList<TestFailure> Failures)
{
    /// <summary>既没通过也没判定为坏的成员数 —— 也就是"没查完"的那部分。</summary>
    public int Unexamined => Math.Max(0, Members - Verified - Failures.Count);

    public bool Passed => Failures.Count == 0 && Verified == Members;
}

/// <summary>
/// 完整性测试（FR-3.6 “测试压缩包”）：解压并校验 CRC，但不落地任何文件。
/// <para>
/// 旧实现按扩展名分发，并对 ZIP 手工跑 CRC 循环；现在整个包交给引擎的
/// <c>test_only</c> 模式，因此引擎认得的所有格式一视同仁。数据损坏时引擎的错误码会被
/// <c>NativeEngine</c> 翻译成 <see cref="InvalidDataException"/>，与既有契约一致。
/// </para>
/// <para>
/// 两条路：<see cref="Test"/> 只回答"过没过"（压缩完顺手验一遍用这个，一次读盘）；
/// <see cref="TestAll"/> 回答"坏的是哪几个"，见它的注释。
/// </para>
/// </summary>
public static class ArchiveTester
{
    /// <summary>
    /// 一次最多列出的坏成员数。封顶不是因为后面不坏了，而是每认出一个坏成员就要把剩下的
    /// 重读一遍（固实块尤其贵），一个"几乎每个成员都坏"的包能把这一步变成几十次全量重压。
    /// 到数就停，剩下的算进 <see cref="ArchiveTestReport.Unexamined"/>。
    /// </summary>
    public const int MaxFailures = 100;

    public static void Test(
        string archivePath,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive to test was not found.", fullPath);
        }

        NativeEngine.Run("test archive", () =>
        {
            using var single = SingleFileArchive.Open(fullPath, cancellationToken);
            using var reader = ArchiveReader.OpenNative(single.FilePath, password);

            reader.Test(Options(password, progress));
            cancellationToken.ThrowIfCancellationRequested();
        }, badFormatMeansUnsupported: true);
    }

    /// <summary>
    /// 把整包读一遍，并且<strong>把每个坏成员都列出来</strong>。
    /// <para>
    /// 引擎的 <c>test_only</c> 在第一个坏成员上就停了 —— 这对"压完顺手测一下"够用，
    /// 但对一个从网盘下下来、下载断过两次的包不够：用户要知道的是<strong>哪些</strong>文件还能用，
    /// 而不是"第一个撞墙的是谁"。所以这里整包先读一遍（好包就这一次），失败之后
    /// 拿同一个句柄从下一个成员接着读，把每一处失败都收下来。
    /// </para>
    /// <para>
    /// 代价写在明面上：每认出一个坏成员，剩下的就要再读一遍；固实（solid）7z 里这意味着
    /// 从压缩块开头重解一次，界面上那根进度条会往回走。这不是 bug，是真的在重读 ——
    /// 而 ZIP 的成员各压各的，重读只重读剩下的那几个，条不会退。
    /// </para>
    /// <para>口令错、格式不认识这些"根本没读成"的情况照旧抛异常，不进这份报告 ——
    /// 那不是坏成员清单，是"这件事没做成"。</para>
    /// </summary>
    public static ArchiveTestReport TestAll(
        string archivePath,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive to test was not found.", fullPath);
        }

        return NativeEngine.Run("test archive", () =>
        {
            using var single = SingleFileArchive.Open(fullPath, cancellationToken);
            using var reader = ArchiveReader.OpenNative(single.FilePath, password);
            var members = DataMembers(reader);

            try
            {
                reader.Test(Options(password, progress));
                return new ArchiveTestReport(members.Count, members.Count, []);
            }
            catch (AndzipException ex) when (IsDataDamage(ex))
            {
                // 坏包才走到这里。第一遍的进度已经报过了，第二遍从"下一个成员"接着数。
                //
                // 为什么不是 catch (InvalidDataException)：那层翻译在 NativeEngine.Run 的边界上，
                // 而这里还在它的 lambda 里面 —— 手上拿到的就是 AndzipException。在这儿"顺手抓个
                // 宽异常"会把口令错、格式不认识也算成坏成员，那份名单就不可信了。
            }

            return Enumerate(reader, members, password, progress, cancellationToken);
        }, badFormatMeansUnsupported: true);
    }

    /// <summary>
    /// 这个错误是不是"某个成员的数据坏了"。<see cref="AzResult.ErrorBadFormat"/> 在测试与解压
    /// 阶段就是损坏（"打开"阶段它才是"格式不认识"，那一层由 <c>badFormatMeansUnsupported</c> 管）。
    /// </summary>
    private static bool IsDataDamage(AndzipException ex)
        => ex.Result is AzResult.ErrorCrc or AzResult.ErrorBadFormat;

    /// <summary>
    /// 逐个续跑收坏成员。<paramref name="members"/> 按引擎给的序号排好，所以"跳过已经查过的"
    /// 只要把窗口往前挪 —— 不需要重新打开归档。
    /// </summary>
    private static ArchiveTestReport Enumerate(
        NativeArchiveReader reader,
        IReadOnlyList<Andzip.Native.ArchiveEntry> members,
        string? password,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var failures = new List<TestFailure>();
        var verified = 0;
        var next = 0;

        while (next < members.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = new ulong[members.Count - next];
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i] = members[next + i].Index;
            }

            try
            {
                reader.Test(Options(password, progress), batch);
                verified += batch.Length;
                break;
            }
            catch (AndzipException ex) when (IsDataDamage(ex))
            {
                var detail = string.IsNullOrWhiteSpace(ex.NativeMessage) ? ex.Message : ex.NativeMessage;
                var named = FailingName(detail);
                var position = named is null ? -1 : IndexOfName(members, named, next);

                if (position < 0)
                {
                    // 内核这句没有点名到清单里的某个成员（"corrupt archive headers" 那一类）。
                    // 报个位置是猜，而猜错的代价是把好成员说成坏的；把它塞进"坏成员名单"又会让
                    // 一句英文原文长得像个文件名。所以原样抛出去 —— 界面照旧显示内核那句话，
                    // 而那份名单不再出现。
                    throw;
                }

                verified += position - next;
                failures.Add(new TestFailure(members[position].Name, detail));
                next = position + 1;

                if (failures.Count >= MaxFailures)
                {
                    break;
                }
            }
        }

        return new ArchiveTestReport(members.Count, verified, failures);
    }

    private static Andzip.Native.ExtractOptions Options(
        string? password,
        IProgress<ArchiveProgress>? progress)
        => NativeEngine.BuildExtractOptions(
            password,
            Andzip.Native.AzOverwrite.Overwrite,
            preserveMTime: false,
            progress,
            ArchiveOperation.Test);

    /// <summary>有数据要校验的成员，按引擎序号升序（目录与 anti-item 没有字节可验）。</summary>
    private static IReadOnlyList<Andzip.Native.ArchiveEntry> DataMembers(NativeArchiveReader reader)
        => [.. reader.Entries.Where(e => !e.IsDirectory && !e.IsAnti).OrderBy(e => e.Index)];

    /// <summary>
    /// 从内核的错误原文里取出成员名。每句都是"<英文短语> '<名字>'"，名字是最后那一段，
    /// 所以取<strong>第一个</strong>引号到<strong>最后一个</strong>引号之间 —— 名字里带撇号时
    /// （<c>it's.txt</c>）取"最后一对引号"会切错。
    /// </summary>
    private static string? FailingName(string detail)
    {
        var first = detail.IndexOf('\'');
        var last = detail.LastIndexOf('\'');
        return first >= 0 && last > first ? detail[(first + 1)..last] : null;
    }

    private static int IndexOfName(
        IReadOnlyList<Andzip.Native.ArchiveEntry> members,
        string name,
        int from)
    {
        for (var i = from; i < members.Count; i++)
        {
            if (string.Equals(members[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
