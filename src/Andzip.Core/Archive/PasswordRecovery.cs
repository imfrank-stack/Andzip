namespace Andzip.Core.Archive;

/// <summary>这个包能不能拿来做口令穷举，以及<strong>该拿什么当判据</strong>。</summary>
public enum PasswordRecoveryTarget
{
    /// <summary>能查：确实有加密内容，且错的口令会被明确拒掉。</summary>
    Searchable,

    /// <summary>这个包里没有任何加密成员 —— 它根本不需要口令，查它是白查。</summary>
    NotEncrypted,

    /// <summary>打不开，也判断不出是"缺口令"还是"文件本身有问题"（坏包 / 不认识的格式）。</summary>
    Unopenable,
}

/// <summary>用哪种方式判定一个候选口令对不对。</summary>
public enum PasswordProbeKind
{
    /// <summary>整个头部是加密的（头加密的 7z）：能打开头部本身就是证据。</summary>
    OpensHeader,

    /// <summary>头部是明文、成员是密文（ZIP、只加密数据的 7z）：必须<strong>把一个成员整块读出来</strong>，
    /// 让引擎去校验 CRC 才算数。</summary>
    ReadsEntry,
}

/// <summary>
/// 开工前对目标包做一次<strong>体检</strong>的结论：能不能查、按哪种方式判、判的时候读哪一格。
/// <para>存在的理由是两件会要命的事：</para>
/// <para>① <strong>不能拿"读前 64 KB 没报错"当口令对了的证据。</strong>
/// ZIP 的加密头里有校验字节，读一点就会拒；但 7z 的 LZMA 流用错口令解出来是<strong>合法的乱码</strong>，
/// 中途不报错，只有整块读完、引擎对完 CRC 才会失败。半路收工 = 报一个错的口令给用户，
/// 而他会当成真的去用。</para>
/// <para>② <strong>不能对着一个根本不是压缩包的文件穷举。</strong>每次尝试都因为同一个原因失败，
/// 却会被记成"口令不对"，跑完一亿个候选最后说"没找到" —— 那句"没找到"是假的。
/// 所以先体检，不合格就当场说清楚，一颗候选都不试。</para>
/// </summary>
public sealed record PasswordRecoveryPlan(
    PasswordRecoveryTarget Target,
    PasswordProbeKind Probe,
    int ProbeEntryIndex,
    long ProbeEntryBytes)
{
    public bool CanSearch => Target == PasswordRecoveryTarget.Searchable;
}

/// <summary>候选口令的来源：一个可按下标取值的序列（穷举空间或字典）。</summary>
public interface IPasswordCandidates
{
    /// <summary>能不能报出精确总数。为假时 <see cref="Count"/> 无意义，进度只能显示"已试多少个"。</summary>
    bool HasExactCount { get; }

    ulong Count { get; }

    string At(ulong index);
}

/// <summary>
/// 字典候选：一行一个口令。
/// <para>空行与重复行在装载时就去掉 —— 留着它们，"跑了 120 万个候选"里有 30 万是同一句，
/// 而界面上的进度会因此骗人。</para>
/// </summary>
public sealed class PasswordDictionary : IPasswordCandidates
{
    private readonly List<string> _words;

    private PasswordDictionary(List<string> words) => _words = words;

    public bool HasExactCount => true;

    public ulong Count => (ulong)_words.Count;

    public string At(ulong index) => _words[(int)index];

    /// <summary>读不进字典时给出<strong>可以直接显示给用户</strong>的原因。</summary>
    public static (PasswordDictionary? Dictionary, string? Error) TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (null, "字典文件不存在。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var word = line.TrimEnd('\r', ' ', '\t');
                if (word.Length > 0 && seen.Add(word))
                {
                    words.Add(word);
                }
            }
        }
        catch (Exception)
        {
            return (null, "字典文件读不出来（被占用、编码不对，或路径指向的是目录）。");
        }

        return words.Count == 0
            ? (null, "这个字典里一个口令都没有。")
            : (new PasswordDictionary(words), null);
    }
}

/// <summary>穷举空间也是一种候选来源（接口实现放在这里，类型本体见 <c>PasswordSpace.cs</c>）。</summary>
public sealed partial class PasswordSpace : IPasswordCandidates
{
    bool IPasswordCandidates.HasExactCount => !IsBeyondReach;

    ulong IPasswordCandidates.Count => Count;
}

/// <summary>
/// 每试完一批之后的进度读数。
/// <para>这里<strong>没有</strong>"总数不知道"那一格：跑不起来的空间在开跑前就被拒了
/// （<see cref="PasswordRecovery.Run"/>），所以进度条永远是走得完的那一根。
/// 一根不知道终点在哪的进度条不是"信息少一点"，是一个会让人一直等下去的假象。</para>
/// </summary>
public sealed record PasswordRecoveryProgress(ulong Attempts, ulong Total, double AttemptsPerSecond)
{
    /// <summary>0~1 的完成比例。</summary>
    public double Fraction => Total == 0 ? 1 : Math.Min(1.0, (double)Attempts / Total);

    /// <summary>按<strong>当前实测速度</strong>估的剩余时间；速度还没量出来时（最后那一次上报）为 null。</summary>
    public TimeSpan? EstimatedRemaining
        => AttemptsPerSecond <= 0
            ? null
            : TimeSpan.FromSeconds((Total - Attempts) / AttemptsPerSecond);
}

public enum PasswordRecoveryOutcome
{
    /// <summary>找到了。</summary>
    Found,

    /// <summary>整个空间试完了，没有命中。</summary>
    Exhausted,

    /// <summary>用户按了停止。<strong>不等于"没找到"</strong>，界面上不能混用这两句。</summary>
    Cancelled,
}

public sealed record PasswordRecoveryResult(
    PasswordRecoveryOutcome Outcome,
    string? Password,
    ulong Attempts,
    TimeSpan Elapsed);
