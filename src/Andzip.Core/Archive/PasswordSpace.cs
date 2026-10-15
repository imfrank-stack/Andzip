using System.Globalization;

namespace Andzip.Core.Archive;

/// <summary>
/// 口令穷举的<strong>候选空间</strong>：长度在 <c>[MinLength, MaxLength]</c> 之间、
/// 每个字符取自同一批字符的所有字符串。
/// <para>它存在的理由是界面上一句必须说真话的文案：「这个空间里有 <strong>多少</strong> 个候选，
/// 按现在的速度要跑 <strong>多久</strong>」。所以这里的核心不是"生成"，而是
/// <see cref="Count"/>（精确到个位，超出可表示范围时如实报"算不下"）与
/// <see cref="At"/>（第 n 个候选是什么 —— 有了它，进度百分比、断点、并行分段才都算得出来）。</para>
/// <para>枚举顺序是<strong>先短后长、同长度内按字符表下标字典序</strong>：
/// 人记得住的口令通常短，先跑短的那一段能在同样的时间里覆盖更多"猜得中"的可能。</para>
/// </summary>
public sealed partial class PasswordSpace
{
    /// <summary>字符表最长允许多少 —— 挡的是"把整个 Unicode 粘进自定义框"这种输入。</summary>
    public const int MaxCharacters = 128;

    /// <summary>长度上限。26 个字符的 15 位就已经装不进 <see cref="ulong"/>，再高没有意义。</summary>
    public const int MaxSupportedLength = 12;

    private readonly string _characters;
    private readonly ulong[] _lengthStart;

    private PasswordSpace(string characters, int minLength, int maxLength, ulong count, bool beyondReach, double log10)
    {
        _characters = characters;
        MinLength = minLength;
        MaxLength = maxLength;
        Count = count;
        IsBeyondReach = beyondReach;
        Log10Size = log10;

        // 每个长度段在整个序列里的起点，供 At() 用。
        _lengthStart = new ulong[maxLength + 1];
        var running = 0UL;
        var saturated = beyondReach;
        for (var length = minLength; length <= maxLength; length++)
        {
            _lengthStart[length] = running;
            if (saturated || !TryPow((ulong)characters.Length, length, out var per)
                || ulong.MaxValue - running < per)
            {
                // 这一段之后再也数不准了：起点写成 MaxValue —— At() 在 IsBeyondReach 下本来就会抛。
                saturated = true;
                running = ulong.MaxValue;
            }
            else
            {
                running += per;
            }
        }
    }

    /// <summary>字符表（界面上要原样显示给用户核对）。</summary>
    public string Characters => _characters;

    public int MinLength { get; }

    public int MaxLength { get; }

    /// <summary>候选总数。<see cref="IsBeyondReach"/> 为真时这个数没有意义（已饱和）。</summary>
    public ulong Count { get; }

    /// <summary>空间大到算不出精确值（超过 64 位）。界面必须据此改口，不能报一个假数字。</summary>
    public bool IsBeyondReach { get; }

    /// <summary>log₁₀(总数)，用于"算不精确也至少告诉你数量级"。</summary>
    public double Log10Size { get; }

    /// <summary>
    /// 建一个空间。字符去重后排序（"aab" 与 "ab" 是同一个空间，用户不该因为多打了一遍就以为空间变大了）。
    /// </summary>
    /// <returns>建不出来时给出<strong>可以直接显示给用户</strong>的原因。</returns>
    public static (PasswordSpace? Space, string? Error) TryCreate(string characters, int minLength, int maxLength)
    {
        var distinct = new string((characters ?? string.Empty).Distinct().Order().ToArray());

        if (distinct.Length == 0)
        {
            return (null, "字符表是空的。");
        }

        if (distinct.Length > MaxCharacters)
        {
            return (null, $"字符表最多 {MaxCharacters} 个，现在写了 {distinct.Length} 个。");
        }

        if (minLength < 1 || maxLength < minLength)
        {
            return (null, "长度范围不对：最短至少 1，且最长不能短于最短。");
        }

        if (maxLength > MaxSupportedLength)
        {
            return (null, $"最长只支持 {MaxSupportedLength} 位 —— 再长的空间连数都数不完，跑了也白跑。");
        }

        // 精确总数：能装进 ulong 就报精确值，装不下就只报数量级。
        var total = 0UL;
        var log10 = 0.0;
        var beyond = false;
        for (var length = minLength; length <= maxLength; length++)
        {
            log10 += length * Math.Log10(distinct.Length);
            if (!beyond && TryPow((ulong)distinct.Length, length, out var per))
            {
                if (ulong.MaxValue - total < per)
                {
                    beyond = true;
                }
                else
                {
                    total += per;
                }
            }
            else
            {
                beyond = true;
            }
        }

        return (new PasswordSpace(distinct, minLength, maxLength, beyond ? ulong.MaxValue : total, beyond, log10), null);
    }

    /// <summary>
    /// 第 <paramref name="index"/> 个候选。<paramref name="index"/> 必须小于 <see cref="Count"/>。
    /// </summary>
    public string At(ulong index)
    {
        if (IsBeyondReach)
        {
            throw new InvalidOperationException("空间大到算不出精确总数，不能按下标取候选。");
        }

        var length = MaxLength;
        while (length > MinLength && _lengthStart[length] > index)
        {
            length--;
        }

        var offset = index - _lengthStart[length];
        var radix = (ulong)_characters.Length;
        var buffer = new char[length];
        for (var position = length - 1; position >= 0; position--)
        {
            buffer[position] = _characters[(int)(offset % radix)];
            offset /= radix;
        }

        return new string(buffer);
    }

    /// <summary>
    /// 把候选总数写成给界面填进占位符的形式（精确时是 <c>1,234,567</c>，
    /// 数不准时是 <c>10^23</c> 这样的幂）。
    /// <para><strong>"约"这个字不在这里</strong>：它是句式的一部分，归语言包
    /// （<c>TEXT_RECOVERY_SPACE_HUGE</c> 自己写着「约 ${size} 个」）。代码里带上它，
    /// 中文里会渲染成「约 约 10^23」，英文里会渲染成「about 约 10^23」—— 一句里两种语言。</para>
    /// </summary>
    public string DescribeCount(IFormatProvider? provider = null)
        => IsBeyondReach
            ? $"10^{(int)Math.Floor(Log10Size)}"
            : Count.ToString("N0", provider ?? CultureInfo.CurrentCulture);

    private static bool TryPow(ulong baseValue, int exponent, out ulong result)
    {
        result = 1UL;
        for (var i = 0; i < exponent; i++)
        {
            if (ulong.MaxValue / baseValue < result)
            {
                result = 0;
                return false;
            }

            result *= baseValue;
        }

        return true;
    }
}
