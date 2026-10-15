using System.Diagnostics;
using NativeArchiveReader = Andzip.Native.ArchiveReader;
using NativeExtractOptions = Andzip.Native.ExtractOptions;

namespace Andzip.Core.Archive;

/// <summary>
/// 口令穷举的执行体（「工具 → 密码恢复」的数据侧）。
/// <para><strong>只读，不写</strong>：口令自始至终只出现在返回值与内存里，绝不进
/// <c>settings.ini</c>、注册表或日志 —— 与 <see cref="PasswordCache"/> 同一条底线。
/// 找到之后由界面显示给用户，要不要顺手记进本次运行的口令表是用户在屏幕上另一次决定。</para>
/// <para><strong>进度回调那一份必须是 UI 自己造的 sink</strong>（<c>ProgressWindow.CreateSink()</c>
/// 那一类）。直接把一个 <c>Progress&lt;T&gt;</c> 传进来，它捕获的同步上下文可能是默认的，
/// 回调就会落到线程池上 —— 0.9.12 那次跨线程闪退就是这么来的。</para>
/// </summary>
public static class PasswordRecovery
{
    /// <summary>体检时"试一个肯定不对的口令"用的那一句。带空格，不会与任何正常口令撞上。</summary>
    private const string DefinitelyWrong = "andzip 体检用的错口令 ✗";

    /// <summary>
    /// 开工前的体检。三种不合格各有各自的实话，<strong>不要混成一句"没找到"</strong>。
    /// </summary>
    public static PasswordRecoveryPlan Inspect(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var full = Path.GetFullPath(archivePath);
        if (!File.Exists(full))
        {
            return Unopenable();
        }

        // 头部明文：不给口令也能列清单。那就顺便挑出"最小的那个加密成员"当探针。
        try
        {
            var probe = ProbeEntry(Native(() => ArchiveReader.OpenNative(full, null)));
            return probe is null
                ? new PasswordRecoveryPlan(PasswordRecoveryTarget.NotEncrypted, PasswordProbeKind.ReadsEntry, -1, 0)
                : new PasswordRecoveryPlan(PasswordRecoveryTarget.Searchable, PasswordProbeKind.ReadsEntry, probe.Value.Index, probe.Value.Bytes);
        }
        catch (Exception ex) when (EncryptedHeaderProbe.LooksLikeMissingPassword(ex, full))
        {
        }
        catch (Exception)
        {
            return Unopenable();
        }

        // 头部加密：能打开头部本身就是判据。但先确认"错的口令确实会被拒" ——
        // 万一这个包其实是别的原因打不开，穷举就会把每一次失败都记成"口令不对"。
        if (Rejected(full, DefinitelyWrong))
        {
            return new PasswordRecoveryPlan(PasswordRecoveryTarget.Searchable, PasswordProbeKind.OpensHeader, -1, 0);
        }

        return Unopenable();
    }

    /// <summary>
    /// 这个候选口令能不能把包<strong>真正解开</strong>。
    /// <para>判据是"整块读完一个成员"而不是"读开头几 KB 没报错"：7z 的 LZMA 流用错口令解出来
    /// 是合法的乱码，中途不报错，只有 CRC 对上才算数。半路收工会报一个错的口令给用户。</para>
    /// </summary>
    public static bool Accepts(string archivePath, string candidate, PasswordRecoveryPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanSearch)
        {
            return false;
        }

        var full = Path.GetFullPath(archivePath);
        try
        {
            Native(() =>
            {
                using var reader = ArchiveReader.OpenNative(full, candidate);
                if (plan.Probe == PasswordProbeKind.ReadsEntry)
                {
                    // 只测探针那一格：整包测一遍的话，一次尝试的成本随包大小涨，
                    // 而"口令对不对"这件事一个成员就答得完。
                    reader.Test(
                        new NativeExtractOptions { Password = candidate, PasswordPrompt = () => null },
                        [(ulong)plan.ProbeEntryIndex]);
                }

                return true;
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 逐个试。命中就返回，试完返回"没找到"，被取消返回"已停止"—— 三句话在界面上必须是三句。
    /// <para>速度是<strong>现场量</strong>的（同一个包、同一个判据），不是抄一个常数：
    /// ZIP 与只加密数据的 7z 大约五千次一秒，头加密的 7z 只有两百次一秒，
    /// 而探针那一格很大时还会更低。ETA 用实测值算，才不会出现"预计 3 秒、实际跑了三小时"。</para>
    /// </summary>
    public static PasswordRecoveryResult Run(
        string archivePath,
        IPasswordCandidates candidates,
        PasswordRecoveryPlan plan,
        IProgress<PasswordRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var started = Stopwatch.StartNew();

        // 体检没过就不许"跑一遍然后说没有"：那不是穷举的结论，是没资格穷举。
        if (!plan.CanSearch)
        {
            throw new ArgumentException(
                "这个目标没通过开工前的体检（打不开，或根本没有加密内容），不能拿它做穷举。",
                nameof(plan));
        }

        // 按下标枚举要求总数是精确的 —— 数不出总数的空间给不出进度、也给不出还要跑多久，
        // 界面对它的正确回答是"不给开始"，而不是开一根不知道要走多久的进度条。
        if (!candidates.HasExactCount)
        {
            throw new ArgumentException(
                "候选总数不是精确值，不能按下标枚举。请收窄字符集或长度。",
                nameof(candidates));
        }

        var total = candidates.Count;
        var attempts = 0UL;
        var lastReport = Stopwatch.GetTimestamp();

        for (var index = 0UL; index < total; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new PasswordRecoveryResult(PasswordRecoveryOutcome.Cancelled, null, attempts, started.Elapsed);
            }

            var candidate = candidates.At(index);
            attempts++;

            if (Accepts(archivePath, candidate, plan))
            {
                return new PasswordRecoveryResult(PasswordRecoveryOutcome.Found, candidate, attempts, started.Elapsed);
            }

            if (Stopwatch.GetElapsedTime(lastReport) >= ReportEvery)
            {
                lastReport = Stopwatch.GetTimestamp();
                progress?.Report(new PasswordRecoveryProgress(
                    attempts, total, attempts / Math.Max(0.001, started.Elapsed.TotalSeconds)));
            }
        }

        progress?.Report(new PasswordRecoveryProgress(attempts, total, 0));
        return new PasswordRecoveryResult(PasswordRecoveryOutcome.Exhausted, null, attempts, started.Elapsed);
    }

    /// <summary>进度最少隔这么久报一次；一秒报几十次只会把 UI 淹掉。</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>给一个肯定不对的口令，头部是不是真的被拒。拒不掉就说明"打不开"另有原因，不能当缺口令来穷举。</summary>
    private static bool Rejected(string fullPath, string candidate)
    {
        try
        {
            Native(() =>
            {
                using var reader = ArchiveReader.OpenNative(fullPath, candidate);
                return reader.EntryCount;
            });
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static PasswordRecoveryPlan Unopenable()
        => new(PasswordRecoveryTarget.Unopenable, PasswordProbeKind.OpensHeader, -1, 0);

    /// <summary>
    /// 挑<strong>最小的非目录加密成员</strong>当探针：一次尝试要读的字节数最少，
    /// 而"整块读完 + 对 CRC"这个判据一分不损失。
    /// </summary>
    private static (int Index, long Bytes)? ProbeEntry(NativeArchiveReader reader)
    {
        (int Index, long Bytes)? best = null;
        foreach (var entry in reader.Entries)
        {
            if (entry.IsDirectory || entry.IsAnti || !entry.IsEncrypted)
            {
                continue;
            }

            var bytes = (long)(entry.Size ?? entry.PackedSize ?? 0UL);
            if (best is null || bytes < best.Value.Bytes)
            {
                best = ((int)entry.Index, bytes);
            }
        }

        return best;
    }

    /// <summary>
    /// 内核调用一律走 <see cref="NativeEngine"/>：它负责 COM 套间与错误翻译。
    /// </summary>
    private static T Native<T>(Func<T> call) => NativeEngine.Run("try a password", call);
}
